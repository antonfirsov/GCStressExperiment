using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GCStressExperiment
{
    class Program
    {
        private static List<byte[]> _gcArrays = new List<byte[]>();
        private static List<IntPtr> _nativeCrap = new List<IntPtr>();
        private static DateTime _lastGc = DateTime.MinValue;
        private static bool _paused = false;
        private static bool _aborted = false;

        static void Main(string[] args)
        {
            Console.WriteLine("'Esc' to abort, 'x' to clear ...");

            (bool managed, bool unmanaged, double maxMegabytes, double allocationStep) = ParseArguments(args);

            Console.WriteLine($"Managed Allocations: {managed} Unmanaged Allocations: {unmanaged} allocationStep: {allocationStep}MB");
            Console.WriteLine($"Is64BitProcess:{Environment.Is64BitProcess} IsServerGC: {GCSettings.IsServerGC}");
            PrintGCInfo();

            Gen2GcCallback.Register(OnGen2Gc);

            _ = Task.Run(() => RunAllocations(managed, unmanaged, maxMegabytes, allocationStep));

            while (true)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey();
                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    _aborted = true;
                    return;
                }
                else if (keyInfo.Key == ConsoleKey.X)
                {
                    lock (_gcArrays)
                    {
                        _gcArrays.Clear();
                        foreach (IntPtr p in _nativeCrap)
                        {
                            Marshal.FreeHGlobal(p);
                        }
                        _nativeCrap.Clear();
                        GC.Collect();
                        _totalRetainedMb = 0;
                    }
                }
                else if (keyInfo.Key == ConsoleKey.G)
                {
                    GC.Collect();
                    PrintGCInfo();
                }
                else if (keyInfo.Key == ConsoleKey.I)
                {
                    PrintGCInfo();
                }
                else if (keyInfo.Key == ConsoleKey.P)
                {
                    _paused = !_paused;
                    Console.WriteLine($"Paused: {_paused}");
                }
                else if (keyInfo.Key == ConsoleKey.A)
                {
                    _ = new byte[64 * 1024];
                }
            }
        }

        private static bool OnGen2Gc()
        {
            TimeSpan dt = DateTime.Now - _lastGc;
            _lastGc = DateTime.Now;
            Console.WriteLine($"------ Gen2 GC after {dt.TotalSeconds} sec -----");
            PrintGCInfo();
            Console.WriteLine("--------------------");
            return true;
        }

        private static (bool managed, bool unmanaged, double maxMegabytes, double allocationStep) ParseArguments(string[] args)
        {
            bool managed = args.Any(a => a.Equals("m", StringComparison.OrdinalIgnoreCase));
            bool unmanaged = args.Any(a => a.Equals("u", StringComparison.OrdinalIgnoreCase));
            double maxMegabytes = double.MaxValue;
            double allocationStep = 16;
            bool foundMaxMb = false;
            bool foundAllocationStep = false;

            foreach (string a in args)
            {
                if (!foundMaxMb && double.TryParse(a, out maxMegabytes))
                {
                    foundMaxMb = true;
                }
                else if (!foundAllocationStep && 
                    a.StartsWith("a", StringComparison.OrdinalIgnoreCase) && 
                    double.TryParse(a.AsSpan().Slice(1), out allocationStep))
                {
                    foundAllocationStep = true;
                }
            }
            maxMegabytes = foundMaxMb ? maxMegabytes : double.MaxValue;
            allocationStep = foundAllocationStep ? allocationStep : 16;
            return (managed, unmanaged, maxMegabytes, allocationStep);
        }

        private static void PrintGCInfo()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            string str = $"HeapSize:{GBs(info.HeapSizeBytes)}, TotalAvailableMemory:{GBs(info.TotalAvailableMemoryBytes)}, HighMemoryLoadThreshold:{GBs(info.HighMemoryLoadThresholdBytes)}, MemoryLoad:{GBs(info.MemoryLoadBytes)}";
            Console.WriteLine(str);
        }

        private static double _totalRetainedMb = 0;

        private static void RunAllocations(bool managed, bool unmanaged, double maxMegabytes, double allocationStep)
        {
            try
            {
                MicroTimer wait = new MicroTimer(TimeSpan.FromMilliseconds(allocationStep) * 5);

                int allocationBytes = (int)(allocationStep*1024*1024);

                double allocationIncrement = 0;
                if (managed) allocationIncrement += MB(allocationBytes);
                if (unmanaged) allocationIncrement += MB(allocationBytes);

                for (int i = 0; i < 10000; i++)
                {
                    if (_aborted) return;
                    if (_paused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    lock (_gcArrays)
                    {
                        if (managed)
                        {
                            byte[] array = new byte[allocationBytes];
                            _gcArrays.Add(array);
                        }

                        if (unmanaged)
                        {
                            IntPtr h = Marshal.AllocHGlobal(allocationBytes);
                            _nativeCrap.Add(h);
                        }

                        _totalRetainedMb += allocationIncrement;
                        if (managed && _totalRetainedMb > maxMegabytes)
                        {
                            byte[] stuff = _gcArrays[0];
                            _gcArrays.RemoveAt(0);
                            _totalRetainedMb -= MB(stuff.Length);
                        }

                        if (i % 8 == 0)
                        {
                            PrintStatus();
                        }
                    }

                    wait.WaitOne();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RunAllocations failed: {ex.Message}\n{ex.StackTrace}");
                PrintStatus();
                PrintGCInfo();
            }
        }

        private static void PrintStatus()
        {
            Process p = Process.GetCurrentProcess();
            double workingSet = MB(p.WorkingSet64);
            double gcTotal = MB(GC.GetTotalMemory(false));
            double virtualMem = MB(p.PeakVirtualMemorySize64);

            Console.WriteLine($"Retained: {_totalRetainedMb} MB, GC Total: {gcTotal} MB, WorkingSet: {workingSet} MB");
        }

        static double MB(long bytes) => (double)bytes / 1024.0 / 1024.0;
        static double GB(long bytes) => (double)bytes / 1024.0 / 1024.0 / 1024.0;
        static string GBs(long bytes) => string.Format("{0:F1}GB", GB(bytes));
    }

    internal struct MicroTimer
    {
        private readonly TimeSpan _dt;
        private DateTime _next;

        public MicroTimer(TimeSpan dt)
        {
            _dt = dt;
            _next = DateTime.Now + _dt;
        }

        public void Reset() => _next = DateTime.Now + _dt;

        public void WaitOne(CancellationToken cts = default)
        {
            while (DateTime.Now < _next && !cts.IsCancellationRequested) ;
            _next += _dt;
        }
    }
}
