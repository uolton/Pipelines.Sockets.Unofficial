﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Pipelines.Sockets.Unofficial
{
    public partial class SocketConnection
    {
        private SocketAwaitable _writerAwaitable;

        /// <summary>
        /// The total number of bytes sent to the socket
        /// </summary>
        public long BytesSent { get; private set; }

        private async void DoSendAsync()
        {
            Exception error = null;
            DebugLog("starting send loop");
            SocketAsyncEventArgs args = null;
            try
            {
                while (true)
                {
                    DebugLog("awaiting data from pipe...");
                    if(_sendToSocket.Reader.TryRead(out var result))
                    {
                        Helpers.Incr(Counter.SocketPipeReadReadSync);
                    }
                    else
                    {
                        Helpers.Incr(Counter.OpenSendReadAsync);
                        var read = _sendToSocket.Reader.ReadAsync();
                        Helpers.Incr(read.IsCompleted ? Counter.SocketPipeReadReadSync : Counter.SocketPipeReadReadAsync);
                        result = await read;
                        Helpers.Decr(Counter.OpenSendReadAsync);
                    }
                    var buffer = result.Buffer;

                    if (result.IsCanceled || (result.IsCompleted && buffer.IsEmpty))
                    {
                        DebugLog(result.IsCanceled ? "cancelled" : "complete");
                        break;
                    }

                    try
                    {
                        if (!buffer.IsEmpty)
                        {
                            if (args == null) args = CreateArgs(_sendOptions.WriterScheduler, out _writerAwaitable);
                            DebugLog($"sending {buffer.Length} bytes over socket...");
                            Helpers.Incr(Counter.OpenSendWriteAsync);
                            var send = DoSendAsync(Socket, args, buffer, Name);
                            Helpers.Incr(send.IsCompleted ? Counter.SocketSendAsyncSync : Counter.SocketSendAsyncAsync);
                            BytesSent += await send;
                            Helpers.Decr(Counter.OpenSendWriteAsync);
                        }
                        else if (result.IsCompleted)
                        {
                            DebugLog("completed");
                            break;
                        }
                    }
                    finally
                    {
                        DebugLog("advancing");
                        _sendToSocket.Reader.AdvanceTo(buffer.End);
                    }
                }
                TrySetShutdown(PipeShutdownKind.WriteEndOfStream);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                TrySetShutdown(PipeShutdownKind.WriteSocketError, ex.SocketErrorCode);
                DebugLog($"fail: {ex.SocketErrorCode}");
                error = null;
            }
            catch (SocketException ex)
            {
                TrySetShutdown(PipeShutdownKind.WriteSocketError, ex.SocketErrorCode);
                DebugLog($"fail: {ex.SocketErrorCode}");
                error = ex;
            }
            catch (ObjectDisposedException)
            {
                TrySetShutdown(PipeShutdownKind.WriteDisposed);
                DebugLog("fail: disposed");
                error = null;
            }
            catch (IOException ex)
            {
                TrySetShutdown(PipeShutdownKind.WriteIOException);
                DebugLog($"fail - io: {ex.Message}");
                error = ex;
            }
            catch (Exception ex)
            {
                TrySetShutdown(PipeShutdownKind.WriteException);
                DebugLog($"fail: {ex.Message}");
                error = new IOException(ex.Message, ex);
            }
            finally
            {
                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                _sendAborted = true;
                try
                {
                    DebugLog($"shutting down socket-send");
                    Socket.Shutdown(SocketShutdown.Send);
                }
                catch { }

                // close *both halves* of the send pipe; we're not
                // listening *and* we don't want anyone trying to write
                DebugLog($"marking {nameof(Output)} as complete");
                try { _sendToSocket.Writer.Complete(error); } catch { }
                try { _sendToSocket.Reader.Complete(error); } catch { }

                if (args != null) try { args.Dispose(); } catch { }
            }
            DebugLog(error == null ? "exiting with success" : $"exiting with failure: {error.Message}");
            //return error;
        }

        private static SocketAwaitable DoSendAsync(Socket socket, SocketAsyncEventArgs args, ReadOnlySequence<byte> buffer, string name)
        {
            if (buffer.IsSingleSegment)
            {
                return DoSendAsync(socket, args, buffer.First, name);
            }

#if SOCKET_STREAM_BUFFERS
            if (!args.MemoryBuffer.IsEmpty)
#else
            if (args.Buffer != null)
#endif
            {
                args.SetBuffer(null, 0, 0);
            }

            args.BufferList = GetBufferList(args, buffer);

            Helpers.DebugLog(name, $"## {nameof(socket.SendAsync)} {buffer.Length}");
            SocketAwaitable.Reset(args);
            if (socket.SendAsync(args))
            {
                Helpers.Incr(Counter.SocketSendAsyncMultiAsync);
            }
            else
            {
                Helpers.Incr(Counter.SocketSendAsyncMultiSync);
                SocketAwaitable.OnCompleted(args);
            }

            return GetAwaitable(args);
        }

        private static SocketAwaitable DoSendAsync(Socket socket, SocketAsyncEventArgs args, ReadOnlyMemory<byte> memory, string name)
        {
            // The BufferList getter is much less expensive then the setter.
            if (args.BufferList != null)
            {
                args.BufferList = null;
            }

#if SOCKET_STREAM_BUFFERS
            args.SetBuffer(MemoryMarshal.AsMemory(memory));
#else
            var segment = memory.GetArray();

            args.SetBuffer(segment.Array, segment.Offset, segment.Count);
#endif
            Helpers.DebugLog(name, $"## {nameof(socket.SendAsync)} {memory.Length}");
            SocketAwaitable.Reset(args);
            if (socket.SendAsync(args))
            {
                Helpers.Incr(Counter.SocketSendAsyncSingleAsync);
            }
            else
            {
                Helpers.Incr(Counter.SocketSendAsyncSingleSync);
                SocketAwaitable.OnCompleted(args);
            }

            return GetAwaitable(args);
        }

        private static List<ArraySegment<byte>> GetBufferList(SocketAsyncEventArgs args, ReadOnlySequence<byte> buffer)
        {
            Helpers.Incr(Counter.SocketGetBufferList);
            Debug.Assert(!buffer.IsEmpty);
            Debug.Assert(!buffer.IsSingleSegment);

            var list = (args?.BufferList as List<ArraySegment<byte>>) ?? GetSpareBuffer();

            if (list == null)
            {
                list = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                list.Clear();
            }

            foreach (var b in buffer)
            {
                list.Add(b.GetArray());
            }

            return list;
        }
    }
}