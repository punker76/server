﻿#region

using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Ionic.Zlib;
using UlteriusServer.Api.Network.Messages;
using UlteriusServer.Api.Services.LocalSystem;
using UlteriusServer.Api.Services.ScreenShare;
using UlteriusServer.Api.Win32.WindowsInput.Native;
using UlteriusServer.WebSocketAPI.Authentication;
using vtortola.WebSockets;

#endregion

namespace UlteriusServer.Api.Network.PacketHandlers
{
    internal class ScreenSharePacketHandler : PacketHandler
    {

        private readonly Screen[] _screens = Screen.AllScreens;
        private readonly ScreenShareService _shareService = UlteriusApiServer.ScreenShareService;
        private AuthClient _authClient;
        private MessageBuilder _builder;
        private WebSocket _client;
        private Packet _packet;


        public void StopScreenShare()
        {
            try
            {
                var streamThread = ScreenShareService.Streams[_authClient];
                if (streamThread == null || !streamThread.IsAlive) return;
                streamThread.Abort();
                Thread outtemp;
                ScreenShareService.Streams.TryRemove(_authClient, out outtemp);
                CleanUp();
                if (!_client.IsConnected) return;
                var data = new
                {
                    streamStopped = true
                };
                _builder.WriteMessage(data);
            }
            catch (Exception e)
            {
                if (_client.IsConnected)
                {
                    var data = new
                    {
                        streamStopped = false,
                        message = e.Message
                    };
                    _builder.WriteMessage(data);
                }
            }
        }

        private void CleanUp()
        {
            var keyCodes = Enum.GetValues(typeof(VirtualKeyCode));
            //release all keys
            foreach (var keyCode in keyCodes)
            {
                if (_shareService.Simulator.InputDeviceState.IsKeyDown((VirtualKeyCode) keyCode))
                {
                    _shareService.Simulator.Keyboard.KeyUp((VirtualKeyCode) keyCode);
                }
            }
        }

        public void CheckServer()
        {
        }

        public void StartScreenShare()
        {
            try
            {
                if (ScreenShareService.Streams.ContainsKey(_authClient))
                {
                    var failData = new
                    {
                        cameraStreamStarted = false,
                        message = "Stream already created"
                    };
                    _builder.WriteMessage(failData);
                    return;
                }
                var stream = new Thread(GetScreenFrame) {IsBackground = true};
                ScreenShareService.Streams[_authClient] = stream;
                var data = new
                {
                    screenStreamStarted = true
                };
                _builder.WriteMessage(data);
                ScreenShareService.Streams[_authClient].Start();
            }
            catch (Exception exception)
            {
                var data = new
                {
                    cameraStreamStarted = false,
                    message = exception.Message
                };

                _builder.WriteMessage(data);
            }
        }

        private void GetScreenFrame()
        {
           
            var lastClipBoard = string.Empty;
            while (_client != null && _client.IsConnected)
            {
                try
                {

                    using (var image = ScreenData.LocalScreen())
                    {
                        if (image == null)
                        {
                            continue;
                        }
                        if (image.Rectangle != Rectangle.Empty)
                        {
                            var data = ScreenData.PackScreenCaptureData(image.ScreenBitmap, image.Rectangle);
                            if (data != null && data.Length > 0)
                            {
                                _builder.Endpoint = "screensharedata";
                                _builder.WriteScreenFrame(data);
                                data = null;
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                        }
                    }
                    var clipboard = ScreenShareService.ClipboardText;
                    if (clipboard.Equals(lastClipBoard) || string.IsNullOrEmpty(clipboard)) continue;
                    var clipBoardData = new
                    {
                        Text = clipboard
                    };
                    _builder.Endpoint = "clipboarddata";
                    _builder.WriteMessage(clipBoardData);
                    lastClipBoard = clipboard;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message + " " + e.StackTrace);
                }
            }
            Console.WriteLine("Screen Share Died");
        }

        public override void HandlePacket(Packet packet)
        {
            _client = packet.Client;
            _authClient = packet.AuthClient;
            _packet = packet;
            _builder = new MessageBuilder(_authClient, _client, _packet.EndPoint, _packet.SyncKey);
            switch (_packet.PacketType)
            {
                case PacketManager.PacketTypes.MouseDown:
                    HandleMouseDown();
                    break;
                case PacketManager.PacketTypes.MouseUp:
                    HandleMouseUp();
                    break;
                case PacketManager.PacketTypes.MouseScroll:
                    HandleScroll();
                    break;
                case PacketManager.PacketTypes.LeftDblClick:
                    break;
                case PacketManager.PacketTypes.KeyDown:
                    HandleKeyDown();
                    break;
                case PacketManager.PacketTypes.KeyUp:
                    HandleKeyUp();
                    break;
                case PacketManager.PacketTypes.FullFrame:
                    HandleFullFrame();
                    break;
                case PacketManager.PacketTypes.RightClick:
                    HandleRightClick();
                    break;
                case PacketManager.PacketTypes.MouseMove:
                    HandleMoveMouse();
                    break;
                case PacketManager.PacketTypes.CheckScreenShare:
                    CheckServer();
                    break;
                case PacketManager.PacketTypes.StartScreenShare:
                    StartScreenShare();
                    break;
                case PacketManager.PacketTypes.StopScreenShare:
                    StopScreenShare();
                    break;
            }
        }

        private void HandleFullFrame()
        {
            using (var ms = new MemoryStream())
            {
                using (var grab = ScreenData.CaptureDesktop())
                {
                    grab.Save(ms, ImageFormat.Jpeg);
                    var imgData = ms.ToArray();
                    var compressed = ZlibStream.CompressBuffer(imgData);

                    var bounds = Screen.PrimaryScreen.Bounds;
                    var screenBounds = new
                    {
                        top = bounds.Top,
                        bottom = bounds.Bottom,
                        left = bounds.Left,
                        right = bounds.Right,
                        height = bounds.Height,
                        width = bounds.Width,
                        x = bounds.X,
                        y = bounds.Y,
                        empty = bounds.IsEmpty,
                        location = bounds.Location,
                        size = bounds.Size
                    };
                    var frameData = new
                    {
                        screenBounds,
                        frameData = compressed.Select(b => (int)b).ToArray()
                    };
                    _builder.WriteMessage(frameData);
                }
            }
        }

        private void HandleKeyUp()
        {
            if (!ScreenShareService.Streams.ContainsKey(_authClient)) return;
            var keyCodes = ((IEnumerable) _packet.Args[0]).Cast<object>()
                .Select(x => x.ToString())
                .ToList();
            var codes =
                keyCodes.Select(code => ToHex(int.Parse(code.ToString())))
                    .Select(hexString => Convert.ToInt32(hexString, 16))
                    .ToList();


            foreach (var code in codes)
            {
                var virtualKey = (VirtualKeyCode) code;
                _shareService.Simulator.Keyboard.KeyUp(virtualKey);
            }
        }


        private string ToHex(int value)
        {
            return $"0x{value:X}";
        }

        private void HandleKeyDown()
        {
            if (!ScreenShareService.Streams.ContainsKey(_authClient)) return;
            var keyCodes = ((IEnumerable) _packet.Args[0]).Cast<object>()
                .Select(x => x.ToString())
                .ToList();
            var codes =
                keyCodes.Select(code => ToHex(int.Parse(code.ToString())))
                    .Select(hexString => Convert.ToInt32(hexString, 16))
                    .ToList();
            foreach (var code in codes)
            {
                var virtualKey = (VirtualKeyCode) code;
                _shareService.Simulator.Keyboard.KeyDown(virtualKey);
            }
        }

        private void HandleScroll()
        {
            if (!ScreenShareService.Streams.ContainsKey(_authClient)) return;
            var delta = Convert.ToInt32(_packet.Args[0], CultureInfo.InvariantCulture);
            delta = ~delta;
            _shareService.Simulator.Mouse.VerticalScroll(delta);
        }

        private void HandleMoveMouse()
        {
            if (!ScreenShareService.Streams.ContainsKey(_authClient)) return;
            try
            {
                int y = Convert.ToInt16(_packet.Args[0], CultureInfo.InvariantCulture);
                int x = Convert.ToInt16(_packet.Args[1], CultureInfo.InvariantCulture);
                var device = _screens[0];
                if (x < 0 || x >= device.Bounds.Width || y < 0 || y >= device.Bounds.Height)
                {
                    return;
                }
                Cursor.Position = new Point(x, y);
            }
            catch
            {
                Console.WriteLine("Error moving mouse");
            }
        }

        private void HandleRightClick()
        {
            if (ScreenShareService.Streams.ContainsKey(_authClient))
            {
                _shareService.Simulator.Mouse.RightButtonClick();
            }
        }

        private void HandleMouseUp()
        {
            if (ScreenShareService.Streams.ContainsKey(_authClient))
            {
                _shareService.Simulator.Mouse.LeftButtonUp();
            }
        }

        private void HandleMouseDown()
        {
            if (ScreenShareService.Streams.ContainsKey(_authClient))
            {
                _shareService.Simulator.Mouse.LeftButtonDown();
            }
        }
    }
}