using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO;

namespace TCPRelay {
    class Program {
        static void Main(string[] args) {
            if (args.Length < 4) {
                Console.Error.WriteLine("TCPRelay 0.0.0.0 9418 127.0.0.1 9417");
                Environment.ExitCode = 1;
                return;
            }
            Program p = new Program();
            p.e1 = new IPEndPoint(IPAddress.Parse(args[0]), int.Parse(args[1]));
            p.e2 = new IPEndPoint(IPAddress.Parse(args[2]), int.Parse(args[3]));
            p.Run();
        }

        IPEndPoint e1, e2;

        Socket s1;

        private void Run() {
            if (e1.AddressFamily == AddressFamily.InterNetworkV6) {
                // http://blogs.msdn.com/b/malarch/archive/2005/11/18/494769.aspx
                s1 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                s1.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, 0);
            }
            else {
                s1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            }
            s1.Bind(e1);
            s1.Listen(3);
            while (true) {
                Socket s2 = s1.Accept();
                Thread t = new Thread(Proxy);
                t.Start(s2);
            }
        }

        Object lck = new object();

        void Proxy(object user) {
            Pair p = new Pair();
            p.inst = Interlocked.Increment(ref inst);
            p.sl = (Socket)user;
            p.sr = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            lock (lck) Console.WriteLine("{0} Connect now", p.Id);

            p.sr.Connect(e2);

            lock (lck) Console.WriteLine("{0} Connected", p.Id);

            Thread tlr = new Thread(Dolr);
            tlr.Start(p);
            Thread trl = new Thread(Dorl);
            trl.Start(p);

            tlr.Join();
            trl.Join();

            lock (lck) Console.WriteLine("{0} Shutdown now", p.Id);

            p.sr.Shutdown(SocketShutdown.Both);
            p.sl.Shutdown(SocketShutdown.Both);

            lock (lck) Console.WriteLine("{0} Close now", p.Id);

            p.sr.Close();
            p.sl.Close();

            lock (lck) Console.WriteLine("{0} Finished", p.Id);
        }

        void Dolr(object user) {
            Pair p = (Pair)user;
            Thru(p, true);
        }
        void Dorl(object user) {
            Pair p = (Pair)user;
            Thru(p, false);
        }

        int inst = 0;

        Stack<byte[]> buffs = new Stack<byte[]>();

        byte[] Alloc() {
            lock (buffs) {
                if (buffs.Count != 0) return buffs.Pop();
            }
            return new byte[10000];
        }

        void Free(byte[] bin) {
            lock (buffs) {
                buffs.Push(bin);
            }
        }

        class Pair {
            public Socket sl, sr;
            public int inst;

            public String Id { get { return String.Format("#{0:0000}", inst); } }
        }

        void Dummy(IAsyncResult ar) {
        }

        ManualResetEvent evNever = new ManualResetEvent(false);

        private void Thru(Pair p, bool lr) {
            Socket s1 = lr ? p.sl : p.sr;
            Socket s2 = lr ? p.sr : p.sl;

            Int64 tot = 0;

            Queue<ArraySegment<byte>> mid = new Queue<ArraySegment<byte>>();
            IAsyncResult ra = null, sa = null;
            bool recvDone = false;
            while (true) {
                if (ra == null && !recvDone) {
                    byte[] bin = Alloc();
                    ra = s1.BeginReceive(bin, 0, bin.Length, SocketFlags.None, Dummy, bin);
                }
                if (sa == null && mid.Count != 0) {
                    ArraySegment<byte> bin = mid.Dequeue();
                    sa = s2.BeginSend(bin.Array, bin.Offset, bin.Count, SocketFlags.None, Dummy, bin);
                }

                if (ra == null && sa == null) {
                    if (!recvDone) System.Diagnostics.Debug.Fail("Recv not done");
                    break;
                }
                int t = WaitHandle.WaitAny(new WaitHandle[] { (ra != null) ? ra.AsyncWaitHandle : evNever, (sa != null) ? sa.AsyncWaitHandle : evNever });

                if (t == 0) {
                    SocketError err;
                    int r = s1.EndReceive(ra, out err);
                    byte[] bin = (byte[])ra.AsyncState;
                    if (err != SocketError.Success) {
                        lock (lck) Console.WriteLine("{0} {1} err {2}", p.Id, lr ? "->" : "<-", err);
                        recvDone |= true;
                        ra = null;
                        Free(bin);
                    }
                    else if (r < 1) {
                        lock (lck) Console.WriteLine("{0} {1} eof", p.Id, lr ? "->" : "<-");
                        recvDone |= true;
                        ra = null;
                        Free(bin);
                    }
                    else {
                        ra = null;
                        lock (lck) Console.WriteLine("{0} {1} {2} ({3:#,##0})", p.Id, lr ? "->" : "<-", r, tot);
                        mid.Enqueue(new ArraySegment<byte>(bin, 0, r));
                        tot += r;
                    }
                }
                if (t == 1) {
                    int s = s2.EndSend(sa);
                    ArraySegment<byte> bin = (ArraySegment<byte>)sa.AsyncState;
                    Free(bin.Array);
                    if (bin.Count != s) throw new EndOfStreamException();
                    sa = null;
                }
            }
        }

    }
}
