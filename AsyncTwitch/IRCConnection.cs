﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AsyncTwitch
{
    public abstract class IRCConnection
    {
        /*
         * Everyone say thanks to umbra for all his help with this!
         */
        #region Private Vars
        private const int BUFFER_SIZE = 8192;
        private readonly byte[] EOF = new byte[] { 13, 10};
        private Encoding Utf8NoBOM = new UTF8Encoding(false);

        private byte[] _buffer = new byte[BUFFER_SIZE];
        private Socket _twitchSocket;
        private Queue<byte[]> _recievedQueue = new Queue<byte[]>();

        private object _readLock = new object();
        private bool _reading = false;
        #endregion

        public abstract void OnConnect();
        public abstract void ProcessMessage(byte[] msg);


        internal void Connect(string host, ushort port)
        {
            _twitchSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _twitchSocket.BeginConnect(host, port, new AsyncCallback(ConnectCallback), null);
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            _twitchSocket.EndConnect(ar);
            if(!_twitchSocket.Connected) return;
            _twitchSocket.BeginReceive(_buffer, 0, BUFFER_SIZE, SocketFlags.None, new AsyncCallback(Recieve), null);
            OnConnect();
        }

        private void Recieve(IAsyncResult ar)
        {
            int byteLength;
            try
            {
                byteLength = _twitchSocket.EndReceive(ar);
                if (byteLength <= 0)
                {
                    //Disconnect
                    return;
                }
            }
            catch (Exception e)
            {
                //Treat NRE and ODE nicer.
                if (e is NullReferenceException || e is ObjectDisposedException)
                {
                    return;
                }
                //Disconnect here
                Console.WriteLine(e.ToString());
                return;
            }

            var recievedBytes = new byte[byteLength];

            //Copy can fail so we wrap in a try catch. If it does our network data isn't worth looking at anymore so we reconnect.
            try
            {
                Array.Copy(_buffer, recievedBytes, recievedBytes.Length); //Free up our buffer as fast as possible.
            }
            catch (Exception e)
            {
                //Disconnect
                Console.WriteLine(e.ToString());
                return;
            }

            //Queue our bytes to send to another thread. Lock the queue for thread safety.
            lock(_recievedQueue)
                _recievedQueue.Enqueue(recievedBytes);

            //lock readlock while we queue the process. 
            lock (_readLock)
            {
                if (!_reading)
                {
                    _reading = true;
                    ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessReceived));
                }
            }

            _twitchSocket.BeginReceive(_buffer, 0, BUFFER_SIZE, SocketFlags.None, new AsyncCallback(Recieve), null);
        }

        internal void Send(String data)
        {
            List<byte> byteData = new List<byte>(Encoding.ASCII.GetBytes(data));
            byteData.AddRange(EOF);
            _twitchSocket.BeginSend(byteData.ToArray(), 0, byteData.Count, 0, new AsyncCallback(SendCallback), null);
        }

        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                int bytesSend = _twitchSocket.EndSend(ar);
            } catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        //Loops through our data until empty.
        private void ProcessReceived(object obj)
        {
            byte[] readBuffer = new byte[BUFFER_SIZE];
            byte[] nextBuffer = { };

            int readBytes = 0;
            int nextBytes = 0;
            int loadBytes = 0;

            while (true)
            {
                if (nextBytes == 0 && readBytes == 0)
                {
                    lock (_recievedQueue)
                    {
                        if (_recievedQueue.Count == 0)
                        {
                            _reading = false;
                            break;
                        }

                        nextBytes = _recievedQueue.Peek().Length;
                        nextBuffer = _recievedQueue.Dequeue();
                    }
                }

                if (readBytes < BUFFER_SIZE && nextBytes != 0)
                {
                    loadBytes = BUFFER_SIZE - readBytes;

                    if (loadBytes > nextBuffer.Length)
                    {
                        loadBytes = nextBuffer.Length;
                    }

                    try
                    {
                        Array.Copy(nextBuffer, 0, readBuffer, readBytes, loadBytes);
                        nextBytes -= loadBytes;
                        readBytes += loadBytes;
                    }
                    catch
                    {
                        //disconnect
                        break;
                    }
                }

                if (readBytes == 0) continue; //If readBytes has no data we wanna skip this loop.

                //By this point readBuffer should have some data we can operate on.
                int offset = FindBytePattern(readBuffer, EOF, 0);
                byte[] processedData = new byte[offset]; //Set the length to offset + 1 to contain a full message.

                try
                {
                    Array.Copy(readBuffer, 0, processedData, 0, offset); //Copy till EOF 
                }
                catch
                {
                    //disconnect 
                    break;
                }

                //processed Data now holds a single IRC message so we will pass that into another function.
                ProcessMessage(processedData);

                //Time to clean up
                try
                {
                    Array.Copy(nextBuffer, loadBytes, nextBuffer, 0, nextBytes); //Anything past the nextBytes is garbage data.
                    //This copy moves our readBuffer forward. This is basically a queue. Offset + 2 is to get rid of the ending CR+LF.
                    Array.Copy(readBuffer, offset + 2, readBuffer, 0, readBytes - (offset + 2));
                    readBytes -= (offset + 2);
                }
                catch
                {
                    //disconnect
                    break;
                }
            }
        }

        //This is really fast for how simple it is.
        public int FindBytePattern(byte[] source, byte[] search, int offset)
        {
            var searchLimit = (source.Length - offset) - search.Length; //If we haven't found a match by this point we wont.

            for (var i = offset; i <= searchLimit; i++)
            {
                var x = 0;
                for (; x < search.Length; x++) //Iterate through the array after index i until we fully match search or find a difference.
                {
                    if (search[x] != source[i + x]) break;
                }

                if (x == search.Length) return i;
            }

            return -1;
        }
    }
}