﻿using Newtonsoft.Json;
using NiceHashMiner.Switching;
using System;
using System.Threading;
using System.Windows.Forms;
using NiceHashMiner.Utils.Guid;
using WebSocketSharp;

namespace NiceHashMiner.Stats
{
    public class NiceHashSocket
    {
        #region JSON Models
#pragma warning disable 649, IDE1006

        private class NiceHashLogin
        {
            public string method = "login";
            public string version;
            public int protocol = 1;
            public string btc;
            public string worker;
            public string group;
            public string rig;
        }
        
#pragma warning restore 649, IDE1006
        #endregion

        private WebSocket _webSocket;
        public bool IsAlive => _webSocket.ReadyState == WebSocketState.Open;
        private bool _attemptingReconnect;
        private bool _connectionAttempted;
        private bool _connectionEstablished;
        private readonly Random _random = new Random();
        private readonly string _address;
        private readonly NiceHashLogin _login = new NiceHashLogin();
        
        public event EventHandler OnConnectionEstablished;
        public event EventHandler<MessageEventArgs> OnDataReceived;
        public event EventHandler OnConnectionLost;

        private static readonly string RigID;

        static NiceHashSocket()
        {
            var guid = Helpers.GetMachineGuid();
            if (guid == null)
            {
                // TODO
                RigID = $"{0}-{Guid.NewGuid()}";
                return;
            }

            var uuid = UUID.V5(UUID.Nil().AsGuid(), $"NHML{guid}");
            var b64 = Convert.ToBase64String(uuid.AsGuid().ToByteArray());
            RigID = $"{0}-{b64}";
        }

        public NiceHashSocket(string address)
        {
            _address = address;
        }

        public void StartConnection(string btc = null, string worker = null, string group = null)
        {
            NHSmaData.InitializeIfNeeded();
            _connectionAttempted = true;
            try
            {
                if (_webSocket == null)
                {
                    _webSocket = new WebSocket(_address, true);
                } else
                {
                    _webSocket.Close();
                }

                _webSocket.OnOpen += (sender, args) => Login(btc, worker, group);
                _webSocket.OnMessage += ReceiveCallback;
                _webSocket.OnError += ErrorCallback;
                _webSocket.OnClose += CloseCallback;
                _webSocket.Log.Level = LogLevel.Debug;
                _webSocket.Log.Output = (data, s) => Helpers.ConsolePrint("SOCKET", data.ToString());
                _webSocket.EnableRedirection = true;
                _webSocket.Connect();
                _connectionEstablished = true;
            } catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        private void ReceiveCallback(object sender, MessageEventArgs e)
        {
            OnDataReceived?.Invoke(this, e);
        }

        private static void ErrorCallback(object sender, ErrorEventArgs e)
        {
            Helpers.ConsolePrint("SOCKET", e.ToString());
        }

        private void CloseCallback(object sender, CloseEventArgs e)
        {
            Helpers.ConsolePrint("SOCKET", $"Connection closed code {e.Code}: {e.Reason}");
            AttemptReconnect();
        }

        private void Login(string btc, string worker, string group)
        {
            try
            {
                if (btc != null) _login.btc = btc;
                if (worker != null) _login.worker = worker;
                if (group != null) _login.group = group;
                _login.rig = RigID;
                var loginJson = JsonConvert.SerializeObject(_login);
                SendData(loginJson);

                OnConnectionEstablished?.Invoke(null, EventArgs.Empty);
            } catch (Exception er)
            {
                Helpers.ConsolePrint("SOCKET", er.ToString());
            }
        }

        // Don't call SendData on UI threads, since it will block the thread for a bit if a reconnect is needed
        public bool SendData(string data, bool recurs = false)
        {
            try
            {
                if (_webSocket != null && IsAlive)
                {
                    // Make sure connection is open
                    // Verify valid JSON and method
                    dynamic dataJson = JsonConvert.DeserializeObject(data);
                    if (dataJson.method == "credentials.set" || dataJson.method == "devices.status" || dataJson.method == "login")
                    {
                        Helpers.ConsolePrint("SOCKET", "Sending data: " + data);
                        _webSocket.Send(data);
                        return true;
                    }
                } else if (_webSocket != null)
                {
                    if (AttemptReconnect() && !recurs)
                    {
                        // Reconnect was successful, send data again (safety to prevent recursion overload)
                        SendData(data, true);
                    } else
                    {
                        Helpers.ConsolePrint("SOCKET", "Socket connection unsuccessfull, will try again on next device update (1min)");
                    }
                } else
                {
                    if (!_connectionAttempted)
                    {
                        Helpers.ConsolePrint("SOCKET", "Data sending attempted before socket initialization");
                    } else
                    {
                        Helpers.ConsolePrint("SOCKET", "webSocket not created, retrying");
                        StartConnection();
                    }
                }
            } catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
            return false;
        }

        private bool AttemptReconnect()
        {
            if (_attemptingReconnect)
            {
                return false;
            }
            if (IsAlive)
            {
                // no reconnect needed
                return true;
            }
            _attemptingReconnect = true;
            var sleep = _connectionEstablished ? 10 + _random.Next(0, 20) : 0;
            Helpers.ConsolePrint("SOCKET", "Attempting reconnect in " + sleep + " seconds");
            // More retries on first attempt
            var retries = _connectionEstablished ? 5 : 25;
            if (_connectionEstablished)
            {
                // Don't wait if no connection yet
                Thread.Sleep(sleep * 1000);
            } else
            {
                // Don't not wait again
                _connectionEstablished = true;
            }
            for (var i = 0; i < retries; i++)
            {
                try
                {
                    _webSocket.Connect();
                    Thread.Sleep(100);
                    if (IsAlive)
                    {
                        _attemptingReconnect = false;
                        return true;
                    }
                }
                catch (InvalidOperationException e)
                {
                    if (e.Message == "A series of reconnecting has failed.")
                    {
                        // Need to recreate websocket
                        Helpers.ConsolePrint("SOCKET", "Recreating socket");
                        _webSocket = null;
                        StartConnection();
                        break;
                    }
                }
                catch (Exception e)
                {
                    Helpers.ConsolePrint("SOCKET", $"Error while attempting reconnect: {e}");
                }
                Thread.Sleep(1000);
            }
            _attemptingReconnect = false;
            OnConnectionLost?.Invoke(null, EventArgs.Empty);
            return false;
        }
    }
}
