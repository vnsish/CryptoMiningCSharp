
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO;
using System.Web;
using System.Web.Script.Serialization;

namespace DetectorFalha
{

    class Process
    {
        public int ID { get; set; }
        public string IP { get; set; }
        public int rank { get; set; }

        public Process(int id, string ip, int rank)
        {
            this.ID = id;
            this.IP = ip;
            this.rank = rank;
        }
    }

    class Request
    {

        public long start { get; set; }
        public long end { get; set; }
        public string timestamp { get; set; }
        public string hash { get; set; }
        public int zeros { get; set; }

        public Request(long start, long end, string timestamp, string hash, int zeros)
        {
            this.start = start;
            this.end = end;
            this.timestamp = timestamp;
            this.hash = hash;
            this.zeros = zeros;
        }

        public Request(string hash, string zeros)
        {
            this.hash = hash;
            this.zeros = Convert.ToInt32(zeros);
        }

    }


    class Program
    {
        private static List<Process> todos = new List<Process>();
        private static List<Process> vivos = new List<Process>();
        private static List<Process> detectados = new List<Process>();
        private static UdpClient client = new UdpClient(6001);

        private static Boolean idle = true;
        private static Boolean interrupt = false;

        private static Process lider = null;

        private static int selfRank = 0;
        private static bool isLeader = false;

        const int SEND_PORT = 6001;
        const int RCV_PORT = 6001;

        private static System.Timers.Timer t = new System.Timers.Timer();

        private static Boolean ans = false;
        private static AutoResetEvent newReq = new AutoResetEvent(false);
        private static AutoResetEvent notleader = new AutoResetEvent(false);
        private static AutoResetEvent leader = new AutoResetEvent(false);
        private static AutoResetEvent blockfound = new AutoResetEvent(false);

        private static bool reqReady = false;

        private static Request req;



        static void Main(string[] args)
        {

            Thread threadenvia = new Thread(new ThreadStart(envia));
            threadenvia.Start();

            Thread processa = new Thread(new ThreadStart(Work));
            processa.Start();

            Thread threadlider = new Thread(new ThreadStart(LeaderThread));
            threadlider.Start();
            
            while (true)
            { }
        }

        static void LeaderThread()
        {
            leader.WaitOne();
            while (true)
            {
                if (isLeader)
                {
                    req = RequestJSON();
                    req.timestamp = GetTimestamp();
                    req.start = 0;
                    newReq.Set();
                    reqReady = true;
                    blockfound.WaitOne();
                    foreach (Process p in vivos)
                    {
                        Send("ProcessInterrupt", p.IP);
                    }
                }
            }

        }

        static void envia()
        {

            int i = 1;
            Dictionary<int, String> ips = new Dictionary<int, string>() { { 3, "192.168.15.28" }, };

            foreach (KeyValuePair<int, string> entry in ips)
            {
                todos.Add(new Process(i++, entry.Value, entry.Key));
            }

            //Tempo de timeout em ms
            t.Interval = 7000;
            t.Elapsed += T_Elapsed;
            t.AutoReset = false;

            string[] msg = new string[2];

            t.Start();

            //Recebe assincrono
            client.BeginReceive(new AsyncCallback(receive), null);
            foreach (Process p in todos)
            {
                Send("HeartbeatRequest", p.IP);
            }

            while (true)
            {

            }
        }

        //Callback do receive
        private static void receive(IAsyncResult ar)
        {


            IPEndPoint ip = new IPEndPoint(IPAddress.Any, RCV_PORT);
            byte[] bytes = client.EndReceive(ar, ref ip);

            string message = Encoding.ASCII.GetString(bytes);
            Console.WriteLine("Recebido: {0} - IP: {1}", message, ip.Address.ToString());

            string msg = Encoding.ASCII.GetString(bytes);

            //Request recebido
            if (bytes[0] == 1) Send(2, ip.Address.ToString());
            if (msg == "HeartbeatRequest") Send("HeartbeatReply", ip.Address.ToString());


            //Reply recebido
            if (bytes[0] == 2) vivos.Add(todos.Find(x => x.IP == ip.Address.ToString()));
            if (msg == "HeartbeatReply")
            {
                if (todos.Find(x => x.IP == ip.Address.ToString()) != null) vivos.Add(todos.Find(x => x.IP == ip.Address.ToString()));
                else
                {
                    todos.Add(new Process(todos.Count + 1, ip.Address.ToString(), todos.Count + 1));
                    vivos.Add(todos.Find(x => x.IP == ip.Address.ToString()));
                }

            }

            //Intervalo recebido
            if (msg.Substring(0, 8) == "Process;")
            {
                string[] sub = msg.Split(';');

                req = new Request(Convert.ToInt64(sub[1]), Convert.ToInt64(sub[2]), sub[3], sub[4], Convert.ToInt32(sub[5]));

                ans = true;
            }

            //Interromper processamento
            if (msg == "ProcessInterrupt")
            {
                interrupt = true;
            }

            //Request de processamento
            if (isLeader && (msg == "ProcessRequest" || msg == "ProcessAnswerNo"))
            {
                while (reqReady == false)
                { }
                if (req.start + 10000000 < 2000000000)
                {
                    Send(String.Format("Process;{0};{1};{2};{3};{4}", Convert.ToString(req.start), Convert.ToString(req.start + 10000000),
                        req.timestamp, req.hash, Convert.ToString(req.zeros)), ip.Address.ToString());
                    req.start += 10000000;
                }
                else
                {
                    req.timestamp = GetTimestamp();
                    req.start = 0;
                    Send(String.Format("Process;{0};{1};{2};{3};{4}", Convert.ToString(req.start), Convert.ToString(req.start + 10000000),
                        req.timestamp, req.hash, Convert.ToString(req.zeros)), ip.Address.ToString());
                    req.start += 10000000;
                }
            }

            if (isLeader && msg.StartsWith("ProcessAnswerYes"))
            {
                if (PostBlock(req.timestamp, msg.Substring(17)) == "success")
                {
                    interrupt = true;
                    blockfound.Set();
                    Console.WriteLine(String.Format("Bloco encontrado - IP: {0}", ip.Address.ToString()));
                }
            }
            client.BeginReceive(new AsyncCallback(receive), null);
        }

        private static String GetTimestamp()
        {
            DateTime baseDate = new DateTime(1970, 1, 1);
            TimeSpan diff = DateTime.Now - baseDate;

            return Convert.ToString(Math.Truncate(diff.TotalMilliseconds));
        }

        private static void Work()
        {
            Thread.Sleep(8000);
            while (true)
            {
                interrupt = false;
                if (idle && !isLeader)
                {
                    idle = false;

                    ans = false;
                    do
                    {
                        Send("ProcessRequest", lider.IP);
                        Console.WriteLine("Aguardando reply...");
                        Thread.Sleep(5000);
                    } while (ans == false);


                    long current = req.start;
                    string hash = "";

                    bool found = false;

                    Console.WriteLine(String.Format("Calculando intervalo: {0} - {1}", req.start, req.end));

                    while (current < req.end && !found && !interrupt)
                    {
                        current++;
                        String str = req.hash + Convert.ToString(current) + req.timestamp;
                        hash = ComputeSha256(str);

                        if (TestZeroes(hash, req.zeros))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!interrupt)
                    {
                        if (found)
                        {
                            Console.WriteLine(String.Format("Found\nHash: {0}\nNonce: {1}\nTimestamp: {2}", hash, current, req.timestamp));
                            string msg = "ProcessAnswerYes;" + Convert.ToString(current);
                            Send(msg, lider.IP);
                            Thread.Sleep(5000);
                        }
                        else Send("ProcessAnswerNo", lider.IP);
                    }
                    else
                    {
                        interrupt = false;
                        Thread.Sleep(5000);
                    }
                    idle = true;

                }
                if (idle && isLeader)
                {
                    if (req == null) newReq.WaitOne();
                    idle = false;
                    Request rq;

                    rq = new Request(req.start, req.start + 10000000, req.timestamp, req.hash, req.zeros);
                    req.start += 10000000;

                    long current = rq.start;
                    string hash = "";

                    bool found = false;

                    Console.WriteLine(String.Format("Calculando intervalo: {0} - {1}", rq.start, rq.end));

                    while (current < rq.end && !found && !interrupt)
                    {
                        current++;
                        String str = rq.hash + Convert.ToString(current) + rq.timestamp;
                        hash = ComputeSha256(str);

                        if (TestZeroes(hash, req.zeros))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        Console.WriteLine(String.Format("Bloco encontrado \nHash: {0}\nNonce: {1}\nTimestamp: {2}", hash, current, req.timestamp));
                        if (PostBlock(rq.timestamp, Convert.ToString(current)) == "success")
                        {
                            blockfound.Set();
                            newReq.WaitOne();
                        }
                    }

                    if (interrupt)
                    {
                        interrupt = false;
                        newReq.WaitOne();
                    }

                    idle = true;

                }
            }
        }

        private static bool TestZeroes(string input, int zeros)
        {
            int count = 0;

            foreach (Char c in input)
            {
                if (c == '0') count++;
                else break;
            }

            if (count >= zeros) return true;

            return false;
        }

        //Evento do Timer
        private static void T_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Console.WriteLine("===\nVivos");
            foreach (Process p in vivos)
            {
                Console.WriteLine(p.IP);
            }
            Console.WriteLine("===");
            foreach (Process p in todos)
            {
                if (!vivos.Contains(p) && !detectados.Contains(p))
                {
                    //Falha e para desabilitado para testes
                    //detectados.Add(p);
                    //Console.WriteLine("Processo {0} falhou (IP: {1})", p.ID, p.IP);
                }

            }
            if (!vivos.Contains(lider) || lider == null || lider.rank > maxrank(vivos).rank)
            {
                lider = maxrank(vivos);
                if (lider != null && lider.rank < selfRank)
                {
                    isLeader = false;
                    Console.WriteLine("Novo lider: Processo {0} - Rank {1}", lider.IP, lider.rank);
                }

                else
                {
                    isLeader = true;
                    leader.Set();
                }
            }

            vivos.Clear();
            foreach (Process p in todos)
            {
                Send("HeartbeatRequest", p.IP);
            }

            t.Start();
        }

        //retorna o lider
        private static Process maxrank(List<Process> processos)
        {
            Process max = null;
            if (processos.Count > 0)
            {
                max = processos.First();
                foreach (Process p in processos)
                {
                    if (p.rank < max.rank)
                        max = p;
                }
            }

            return max;
        }

        //envia binario
        private static void Send(int message, string address)
        {
            UdpClient client = new UdpClient();
            IPEndPoint ip = new IPEndPoint(IPAddress.Parse(address), RCV_PORT);
            byte[] bytes = new byte[1];
            bytes[0] = Convert.ToByte(message);
            //Console.WriteLine(bytes[0]);
            client.Send(bytes, bytes.Length, ip);
            client.Close();
            //if (message==2) Console.WriteLine("Enviado: {0} - IP: {1} ", message, ip.Address.ToString());
        }

        //envia string
        private static void Send(string message, string address)
        {
            UdpClient client = new UdpClient();
            IPEndPoint ip = new IPEndPoint(IPAddress.Parse(address), RCV_PORT);
            byte[] bytes = Encoding.ASCII.GetBytes(message);
            //Console.WriteLine(bytes[0]);
            client.Send(bytes, bytes.Length, ip);
            client.Close();
            Console.WriteLine(String.Format("Enviado: {0} - IP: {1}", message, address));
        }

        private static string ComputeSha256(string rawData)
        {
            // Create a SHA256   
            using (SHA256 sha256Hash = SHA256.Create())
            {
                // ComputeHash - returns byte array  
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

                // Convert byte array to a string   
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private static Request RequestJSON()
        {
            string URL = "https://mineracao-facens.000webhostapp.com/request.php";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(URL);
            request.ContentType = "application/json; charset=utf-8";
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;

            string res;
            using (Stream responseStream = response.GetResponseStream())
            {
                StreamReader reader = new StreamReader(responseStream, Encoding.UTF8);
                res = reader.ReadToEnd();
            }

            var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(res);

            Console.WriteLine("Hash: {0}\nZeros: {1}", obj["hash"], obj["zeros"]);

            Request req = new Request(obj["hash"], obj["zeros"]);

            reqReady = true;
            return req;
        }

        private static String PostBlock(string timestamp, string nonce)
        {
            string URL = String.Format("https://mineracao-facens.000webhostapp.com/submit.php?timestamp={0}&nonce={1}&poolname={2}", timestamp, nonce, "PoolTeste");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(URL);
            request.ContentType = "application/json; charset=utf-8";
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;

            string res;
            using (Stream responseStream = response.GetResponseStream())
            {
                StreamReader reader = new StreamReader(responseStream, Encoding.UTF8);
                res = reader.ReadToEnd();
            }

            var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(res);

            return obj["status"];
        }

    }
}
