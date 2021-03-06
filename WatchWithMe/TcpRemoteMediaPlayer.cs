﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Appccelerate.EventBroker;
using Appccelerate.Events;

namespace WatchWithMe
{
	class TcpRemoteMediaPlayerServer
	{
		public class Client : RemoteMediaPlayer
		{
			protected internal class TcpRemoteMediaPlayerConnectMessage : ConnectMessage
			{
				private readonly List<IPEndPoint> _connectedClients;
				public IEnumerable<IPEndPoint> ConnectedClients { get { return _connectedClients.AsReadOnly(); } }

				public TcpRemoteMediaPlayerConnectMessage(long length, long size, IEnumerable<IPEndPoint> connectedClients) : base(length, size)
				{
					_connectedClients = connectedClients.ToList();
				}

				public TcpRemoteMediaPlayerConnectMessage(byte[] messageBytes) : base(messageBytes)
				{
					using (var messageStream = new MemoryStream(messageBytes))
					using (var reader = new BinaryReader(messageStream))
					{
						var messageType = (MessageType)reader.ReadByte();

						if (messageType != MessageType.Connect)
							throw new ArgumentException();

						/*Length = */reader.ReadInt64();
						/*Size = */reader.ReadInt64();

						while (messageStream.Position < messageStream.Length)
						{
							var ip = IPAddress.Parse(reader.ReadString());
							var port = reader.ReadInt32();

							_connectedClients.Add(new IPEndPoint(ip, port));
						}
					}
				}

				public override byte[] Encode()
				{
					var b = base.Encode();

					using (var memoryStream = new MemoryStream())
					using (var writer = new BinaryWriter(memoryStream))
					{
						writer.Write(b);

						foreach (var e in ConnectedClients)
						{
							writer.Write(e.Address.ToString());
							writer.Write(e.Port);
						}

						return memoryStream.ToArray();
					}
				}
			}

			private readonly TcpRemoteMediaPlayerServer _server;
			private readonly TcpClient _tcpClient;
			private readonly List<IPEndPoint> _clients;

			public Client(TcpClient client, TcpRemoteMediaPlayerServer server)
			{
				EndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
				_tcpClient = client;
				_clients = new List<IPEndPoint>();
				_server = server;

				Task.Run(() => ReceiveClientData());
			}

			public IPEndPoint EndPoint { get; private set; }
			public IEnumerable<IPEndPoint> ConnectedClients { get { return _clients.AsReadOnly(); } }

			private async void ReceiveClientData()
			{
				while (_tcpClient.Connected)
				{
					//Receive the size of the message. If the size is less than 255 bytes (it should rarely, 
					//if ever, be larger than a handful of bytes) then only one byte is needed, but this allows for
					//arbitrarily-large messages (up to the memory limit of the application, in theory)

					var buf = new byte[1];
					var messageSize = 0;

					do
					{
						await _tcpClient.GetStream().ReadAsync(buf, 0, 1);
						messageSize += buf[0];
					} while (buf[0] == 255);

					//Receive the message. Because TCP doesn't guarantee that messages won't be fragmented or that the messages
					//will be delivered atomically, this method ensures that the entire message (and only that many bytes)
					//are written into the buffer, regardless of how many times the stream is accessed to do so.

					buf = new byte[messageSize];
					var bytesRead = 0;

					while (bytesRead < messageSize)
					{
						bytesRead += await _tcpClient.GetStream().ReadAsync(buf, bytesRead, messageSize - bytesRead);
					}

					HandleMessage(buf.ToArray());
				}

				_tcpClient.Close();
				_server.NotifyClientDisconnected(EndPoint);
			}

			private bool _connected;

			private void HandleMessage(byte[] messageBytes)
			{
				var message = RemoteMediaPlayerMessage.Decode(messageBytes);

				switch (message.Type)
				{
					case RemoteMediaPlayerMessage.MessageType.Connect:
						var cm = (TcpRemoteMediaPlayerConnectMessage) message;
						
						if (!_connected)
						{
							_connected = true;
							Connect();
						}

						FileSize = cm.Size;
						FileLength = cm.Length;

						OnConnect(this, EventArgs.Empty); //TODO: Put EndPoint in the EventArgs
						break;

					case RemoteMediaPlayerMessage.MessageType.Sync:
						var synm = (SyncMessage) message;

						Position = TimeSpan.FromSeconds(synm.Position);
						ChangeState(synm.State, this);

						OnSync(this, EventArgs.Empty);
						break;

					case RemoteMediaPlayerMessage.MessageType.StateChange:
						var scm = (StateChangeMessage) message;

						ChangeState(scm.State, this);
						break;

					case RemoteMediaPlayerMessage.MessageType.Seek:
						var sm = (SeekMessage) message;

						_position = TimeSpan.FromSeconds(sm.Position);

						OnSeek(this, EventArgs.Empty);
						break;
				}
			}

			private async Task SendMessage(RemoteMediaPlayerMessage m)
			{
				var buf = m.Encode();
				await _tcpClient.GetStream().WriteAsync(buf, 0, buf.Length);
			}

			public override long FileSize { get; protected set; }

			public override long FileLength { get; protected set; }

			public override void Play()
			{
				Task.WaitAny(SendMessage(new StateChangeMessage(PlayState.Playing)));
			}

			public override void Pause()
			{
				Task.WaitAny(SendMessage(new StateChangeMessage(PlayState.Paused)));
			}

			public override void Stop()
			{
				Task.WaitAny(SendMessage(new StateChangeMessage(PlayState.Stopped)));
			}

			private void Seek(long position)
			{
				Task.WaitAny(SendMessage(new SeekMessage(position)));
			}

			private TimeSpan _position;
			public override TimeSpan Position
			{
				get { return _position; }
				set
				{
					_position = value;
					Seek((long) value.TotalSeconds);
				}
			}

			public override void Connect()
			{
				Task.WaitAny(SendMessage(
					new TcpRemoteMediaPlayerConnectMessage(
						_server._localMediaPlayer.FileLength, _server._localMediaPlayer.FileSize,
						_server._clients.Select(c => c.Key))));
			}

			public override void Sync()
			{
				Task.WaitAny(SendMessage(new SyncMessage((long) _position.TotalSeconds, State)));
			}
		}

		private readonly TcpListener _tcpListener;
		private readonly ConcurrentDictionary<IPEndPoint, Client> _clients;

		public IEnumerable<Client> Clients { get { return _clients.Values.ToList().AsReadOnly(); } }

		private IMediaPlayer _localMediaPlayer;

		public TcpRemoteMediaPlayerServer(IMediaPlayer localMediaPlayer)
		{
			_localMediaPlayer = localMediaPlayer;

			_tcpListener = new TcpListener(IPAddress.Any, 0) {ExclusiveAddressUse = false};
			_clients = new ConcurrentDictionary<IPEndPoint, Client>();

			_tcpListener.Start();

			AcceptClients();
		}

		[EventPublication(@"topic://ClientConnected")]
		public event EventHandler<EventArgs<Client>> ClientConnected;
		private async void AcceptClients()
		{
			while (_tcpListener.Active())
			{
				var tcpClient = await _tcpListener.AcceptTcpClientAsync();
				var client = new Client(tcpClient, this);
				if (_clients.TryAdd(client.EndPoint, client))
					if(ClientConnected != null)
						ClientConnected(this, new EventArgs<Client>(client));
			}
		}

		[EventPublication(@"topic://ClientDisconnected")]
		public event EventHandler<EventArgs<Client>> ClientDisconnected;
		private void NotifyClientDisconnected(IPEndPoint ep)
		{
			Client client;
			if (_clients.TryRemove(ep, out client))
				if(ClientDisconnected != null)
					ClientDisconnected(this, new EventArgs<Client>(client));
			
			//foreach (var c in client.ConnectedClients.Where(e => !_clients.ContainsKey(e))
			//	.Select(e => new Client(new TcpClient(e), this)))
			//	if(_clients.TryAdd(c.EndPoint, c))
			//		c.Connect();
			//	else
			//		Debug.Print("Couldn't add client {0}", c.EndPoint);
		}
	}

	static class TcpListenerExtensions
	{
		public static bool Active(this TcpListener l)
		{
			return (bool) (typeof (TcpListener)).GetProperty("Active",
				BindingFlags.NonPublic | BindingFlags.Instance)
				.GetValue(l);
		}
	}
}
