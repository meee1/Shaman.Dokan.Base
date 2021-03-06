﻿using Shaman.Runtime;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shaman.Dokan
{
    public class MemoryStreamManager
    {
        private Action<Stream> write;
        public MemoryStreamInternal ms;
        public volatile bool completed;
        public DateTime Lastread { get; set; } = DateTime.MinValue;
        public MemoryStreamManager(Action<Stream> write, long? length, string filename)
        {
            this.write = write;
            this.Length = length;
            this.Filename = filename;
        }

        public Stream CreateStream()
        {
            return new ConsumerStream(this);
        }
        public long? Length;

        public string Filename { get; private set; }

        internal int Read(long position, byte[] buffer, int offset, int count)
        {
            if (exception != null) throw exception;
            var waitTime = 8;
            while (ms.Length < position + count && !completed)
            {
                //Console.WriteLine("Read Wait got {0} start {1} count {2} wait {3}", ms.length,position,count, waitTime);
                Interlocked.MemoryBarrier();
                Thread.Sleep(waitTime);
                waitTime *= 2;
                if (waitTime > 500)
                    waitTime = 500;
                if (exception != null) throw exception;
            }

            lock (ms)
            {
                var data = ms.data;
                var tocopy = Math.Max((int)Math.Min(count, ms.Length - position), 0);
                if (position > ms.length) return 0;
                Buffer.BlockCopy(data, (int)position, buffer, offset, tocopy);
                return tocopy;
            }

        }
        private int usageToken;
        public void DecrementUsage()
        {
            lock (this)
            {
                users--;
                if (users <= 0)
                {
                    var tok = this.usageToken; 
                    Timer timer = null;
                    timer = new System.Threading.Timer(dummy =>
                    {
                        timer.Dispose();
                        lock (this)
                        {
                            if (tok == this.usageToken )
                            {
                                while (!this.completed)
                                    Thread.Sleep(100);
                                isdisposed = true;
                                this.ms.Dispose();
                                Console.WriteLine("Disposed.");
                            }
                        }
                    }, null, Configuration_KeepFileInMemoryTimeMs, Timeout.Infinite);
                }
            }
        }
        private volatile Exception exception;
        internal void IncrementUsage()
        {
            lock (this)
            {
                usageToken++;
                users++;
                if (ms == null)
                {
                    ms = new MemoryStreamInternal((int)Length.GetValueOrDefault(32 * 1024), Filename);
                    Task.Run(() =>
                    {
                        try
                        {
                            write(ms);
                            if (this.Length != null && this.Length.Value != ms.length)
                                throw new Exception("Promised length was different from actual length.");
                            this.Length = ms.length;
                        }
                        catch (Exception ex)
                        {
                            this.exception = ex;
                        }
                        finally
                        {
                            this.completed = true;
                        }
                    });
                }
            }
        }

        private int users;
        private bool isdisposed;
        [Configuration]
        private static int Configuration_KeepFileInMemoryTimeMs = 30000;

        public bool IsDisposed => isdisposed;
    }

    public class ConsumerStream : Stream
    {
        public MemoryStreamManager memoryStreamManager;
        private long position;
        static private int lastId;
        private int id;
        
        public ConsumerStream(MemoryStreamManager memoryStreamManager)
        {
            this.memoryStreamManager = memoryStreamManager;
            memoryStreamManager.IncrementUsage();
            id = Interlocked.Increment(ref lastId);
            //Console.WriteLine("Open: " + id);
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => memoryStreamManager.Length ?? throw new NotSupportedException();

        public override long Position
        {
            get => position; set
            {
                memoryStreamManager.Lastread = DateTime.Now;
                if (position < 0) throw new ArgumentException();
                if (memoryStreamManager.Length != null && position > memoryStreamManager.Length) throw new ArgumentException();
                position = value;
            }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            memoryStreamManager.Lastread = DateTime.Now;
            if (released != 0) return 0;
            var r = memoryStreamManager.Read(position, buffer, offset, count);
            position += r;
            return r;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            memoryStreamManager.Lastread = DateTime.Now;
            if (origin == SeekOrigin.Begin) Position = offset;
            else if (origin == SeekOrigin.Current) Position += offset;
            else if (origin == SeekOrigin.End) Position = Length + offset;
            else throw new ArgumentException();
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }


        private int released;
        public override void Close()
        {
            if (Interlocked.Increment(ref released) == 1)
            {
                Console.WriteLine("ConsumerStream Close: " + id + " " + memoryStreamManager.Filename);
                memoryStreamManager.DecrementUsage();
            }
        }
    }
}