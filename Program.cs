using System.Diagnostics;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LaptopCheck;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr h, string text, string caption, uint type);

    static readonly string ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    static string DataDir = "", ReportsDir = "";
    static int Live;
    static volatile bool Seen, Quit;
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = null };

    static async Task Main()
    {
        using var mutex = new Mutex(true, "LaptopCheck-single-instance", out bool first);
        if (!first) return;

        // Everything the app writes lives in one folder and is wiped when the app exits.
        var preferred = Path.Combine(ExeDir, "LaptopCheck-data");
        WipeDir(preferred, 2);   // leftovers from a crash or forced shutdown
        DataDir = PickDir(preferred);
        ReportsDir = Path.Combine(DataDir, "battery");
        Directory.CreateDirectory(ReportsDir);

        HttpListener? srv = null;
        int port = 47831;
        for (int p = 47831; p < 47861 && srv == null; p++)
        {
            try { var t = new HttpListener(); t.Prefixes.Add($"http://localhost:{p}/"); t.Start(); srv = t; port = p; }
            catch { }
        }
        if (srv == null) { MessageBox(IntPtr.Zero, "Could not start the local server (ports 47831-47860 are busy).", "LaptopCheck", 0x10); return; }

        var listener = srv;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { var c = await listener.GetContextAsync(); _ = Task.Run(() => Handle(c)); }
                catch { }
            }
        });

        Launch($"http://localhost:{port}/");

        // Exit when Quit is pressed, or when the window has been gone for 8 s
        // (the page keeps one open /api/live stream; closing the window drops it).
        var started = DateTime.UtcNow;
        DateTime? zero = null;
        while (!Quit)
        {
            await Task.Delay(500);
            var now = DateTime.UtcNow;
            if (!Seen) { if ((now - started).TotalSeconds > 90) break; continue; }
            if (Volatile.Read(ref Live) == 0) { zero ??= now; if ((now - zero.Value).TotalSeconds > 8) break; }
            else zero = null;
        }

        await Task.Delay(300);   // let the Quit response flush
        listener.Stop();
        KillBrowser();
        WipeDir(DataDir, 12);
    }

    static void WipeDir(string d, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            try { if (!Directory.Exists(d)) return; Directory.Delete(d, true); return; }
            catch { Thread.Sleep(500); }
        }
    }

    // Closes any Edge/Chrome process that was started with our private profile, so its files unlock.
    static void KillBrowser()
    {
        try
        {
            var prof = Path.Combine(DataDir, "browser-profile");
            using var s = new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'");
            foreach (ManagementBaseObject o in s.Get())
            {
                var cl = o["CommandLine"] as string;
                if (cl != null && cl.Contains(prof, StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch { }
                }
            }
        }
        catch { }
    }

    static string PickDir(string d)
    {
        try
        {
            Directory.CreateDirectory(d);
            var t = Path.Combine(d, ".w");
            File.WriteAllText(t, "x"); File.Delete(t);
            return d;
        }
        catch
        {
            var f = Path.Combine(Path.GetTempPath(), "LaptopCheck-data");
            Directory.CreateDirectory(f);
            return f;
        }
    }

    // Opens Edge (preinstalled on Win10/11) as a clean app window with its own profile on the USB stick.
    static void Launch(string url)
    {
        var prof = Path.Combine(DataDir, "browser-profile");
        var args = $"--app={url} --user-data-dir=\"{prof}\" --no-first-run --no-default-browser-check " +
                   "--use-fake-ui-for-media-stream --autoplay-policy=no-user-gesture-required --window-size=1180,820";
        foreach (var exe in new[] { "msedge", "chrome" })
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); return; }
            catch { }
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox(IntPtr.Zero, "No browser found. Open " + url + " manually.", "LaptopCheck", 0x10); }
    }

    // ---------- HTTP ----------
    static void Handle(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url!.AbsolutePath;
            var post = c.Request.HttpMethod == "POST";
            bool postOnly = path is "/api/battery/report" or "/api/quit";
            if (postOnly && !post) { Send(c, "{}", code: 405); return; }
            if (post && c.Request.Headers["X-LC"] == null) { Send(c, "{}", code: 403); return; }

            switch (path)
            {
                case "/": SendBytes(c, ReadIndex(), "text/html; charset=utf-8"); break;
                case "/api/live":
                    Seen = true;
                    Interlocked.Increment(ref Live);
                    try
                    {
                        c.Response.ContentType = "text/event-stream";
                        c.Response.Headers["Cache-Control"] = "no-store";
                        c.Response.SendChunked = true;
                        var beat = Encoding.UTF8.GetBytes(":\n\n");
                        while (!Quit)
                        {
                            c.Response.OutputStream.Write(beat, 0, beat.Length);
                            c.Response.OutputStream.Flush();
                            Thread.Sleep(2000);
                        }
                    }
                    catch { }
                    finally { Interlocked.Decrement(ref Live); try { c.Response.Abort(); } catch { } }
                    break;
                case "/api/specs": Json(c, GetSpecs()); break;
                case "/api/battery": Json(c, BatteryInfo()); break;
                case "/api/battery/report": Json(c, GenBattery()); break;
                case "/api/usb": Json(c, UsbList()); break;
                case "/api/quit": Quit = true; Json(c, new { ok = true }); break;
                default:
                    if (path.StartsWith("/reports/"))
                    {
                        var name = Path.GetFileName(Uri.UnescapeDataString(path[9..]));
                        var f = Path.Combine(ReportsDir, name);
                        if (!File.Exists(f)) { Send(c, "not found", "text/plain", 404); break; }
                        SendBytes(c, File.ReadAllBytes(f), f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html" : "text/plain; charset=utf-8");
                    }
                    else Send(c, "not found", "text/plain", 404);
                    break;
            }
        }
        catch (Exception e)
        {
            try { Send(c, JsonSerializer.Serialize(new { error = e.Message }), code: 500); } catch { }
        }
    }

    static void SendBytes(HttpListenerContext c, byte[] b, string type, int code = 200)
    {
        c.Response.StatusCode = code;
        c.Response.ContentType = type;
        c.Response.ContentLength64 = b.Length;
        c.Response.Headers["Cache-Control"] = "no-store";
        c.Response.OutputStream.Write(b, 0, b.Length);
        c.Response.Close();
    }
    static void Send(HttpListenerContext c, string s, string type = "application/json", int code = 200) => SendBytes(c, Encoding.UTF8.GetBytes(s), type, code);
    static void Json(HttpListenerContext c, object o) => SendBytes(c, JsonSerializer.SerializeToUtf8Bytes(o, J), "application/json");

    static byte[] ReadIndex()
    {
        // Dev convenience: an index.html next to the exe overrides the embedded one, so UI tweaks need no rebuild.
        var ext = Path.Combine(ExeDir, "index.html");
        if (File.Exists(ext)) return File.ReadAllBytes(ext);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    // ---------- Hardware ----------
    static List<Dictionary<string, object?>> Q(string cls, string props, string ns = @"root\cimv2", string? where = null)
    {
        var res = new List<Dictionary<string, object?>>();
        try
        {
            var q = $"SELECT {props} FROM {cls}" + (where != null ? " WHERE " + where : "");
            using var s = new ManagementObjectSearcher(ns, q);
            foreach (ManagementBaseObject o in s.Get())
            {
                var row = new Dictionary<string, object?>();
                foreach (var p in props.Split(','))
                {
                    try { row[p] = o[p]; } catch { }
                }
                res.Add(row);
            }
        }
        catch { }
        return res;
    }

    const string StorageNs = @"root\Microsoft\Windows\Storage";

    static object GetSpecs()
    {
        var d = new Dictionary<string, object?>();
        var tasks = new List<Task>();
        void Add(string k, Func<object?> f) => tasks.Add(Task.Run(() => { var v = f(); lock (d) d[k] = v; }));

        Add("system", () => Q("Win32_ComputerSystem", "Manufacturer,Model,TotalPhysicalMemory,PCSystemType").FirstOrDefault());
        Add("cpu", () => Q("Win32_Processor", "Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L3CacheSize"));
        Add("ram", () => Q("Win32_PhysicalMemory", "Manufacturer,Capacity,Speed,ConfiguredClockSpeed,PartNumber,BankLabel,SMBIOSMemoryType"));
        Add("gpu", () => Q("Win32_VideoController", "Name,AdapterRAM,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate"));
        Add("board", () => Q("Win32_BaseBoard", "Manufacturer,Product").FirstOrDefault());
        Add("bios", () => Q("Win32_BIOS", "Manufacturer,SMBIOSBIOSVersion,ReleaseDate,SerialNumber").FirstOrDefault());
        Add("os", () => Q("Win32_OperatingSystem", "Caption,Version,BuildNumber,OSArchitecture").FirstOrDefault());
        Add("disks", () => Q("MSFT_PhysicalDisk", "FriendlyName,Size,MediaType,BusType,HealthStatus,OperationalStatus,SpindleSpeed", StorageNs));
        Add("volumes", () => Q("Win32_LogicalDisk", "DeviceID,VolumeName,Size,FreeSpace,FileSystem,DriveType"));
        Add("net", () => Q("Win32_NetworkAdapter", "Name,NetConnectionStatus", where: "PhysicalAdapter=TRUE"));
        Add("battery", BatteryInfo);

        Task.WaitAll(tasks.ToArray());
        return d;
    }

    static object BatteryInfo()
    {
        var st = Q("BatteryStaticData", "DesignedCapacity,ManufactureName,DeviceName", @"root\wmi").FirstOrDefault();
        var fc = Q("BatteryFullChargedCapacity", "FullChargedCapacity", @"root\wmi").FirstOrDefault();
        var cc = Q("BatteryCycleCount", "CycleCount", @"root\wmi").FirstOrDefault();
        var w = Q("Win32_Battery", "EstimatedChargeRemaining,BatteryStatus,Name").FirstOrDefault();
        return new Dictionary<string, object?>
        {
            ["present"] = w != null || st != null,
            ["designed"] = st?.GetValueOrDefault("DesignedCapacity"),
            ["full"] = fc?.GetValueOrDefault("FullChargedCapacity"),
            ["cycles"] = cc?.GetValueOrDefault("CycleCount"),
            ["charge"] = w?.GetValueOrDefault("EstimatedChargeRemaining"),
            ["status"] = w?.GetValueOrDefault("BatteryStatus"),
            ["name"] = st?.GetValueOrDefault("DeviceName") ?? w?.GetValueOrDefault("Name"),
        };
    }

    static object GenBattery()
    {
        var name = $"battery-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        var file = Path.Combine(ReportsDir, name);
        var psi = new ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{file}\" /duration 14")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var msg = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return new { ok = File.Exists(file), file = name, message = msg.Trim() };
    }

    // USB-attached disks (BusType 7). Used by the USB port test to detect plug/unplug.
    static object UsbList() =>
        Q("MSFT_PhysicalDisk", "FriendlyName,UniqueId,DeviceId", StorageNs, "BusType=7")
            .Select(r => new { id = $"{r.GetValueOrDefault("UniqueId")}|{r.GetValueOrDefault("DeviceId")}", name = r.GetValueOrDefault("FriendlyName")?.ToString() ?? "USB drive" })
            .ToList();

}
