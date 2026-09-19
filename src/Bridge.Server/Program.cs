using System.Diagnostics;
using Bridge.Decode;
using Bridge.Protocol;
using Bridge.Server;

// Drop-in replacement for Avid's ProToolsQuickTimeServer.exe. Pro Tools starts it as
//   ProToolsQuickTimeServer.exe <parentPid>
// and talks to it over the DIPC named pipes. See docs/PROTOCOL.md.

if (args.Length < 1 || !int.TryParse(args[0], out int parentPid) || parentPid <= 0)
{
    Console.Error.WriteLine("usage: ProToolsQuickTimeServer.exe <parent-pid>   (started by Pro Tools)");
    return 0; // the original exits silently too
}

var config = BridgeConfig.Load(Path.Combine(AppContext.BaseDirectory, "bridge.json"));
Log.Debug = config.DebugLog;
Log.Open(config.LogDir);
Log.Info($"pt-ffmpeg-bridge {typeof(QtServer).Assembly.GetName().Version} started, parent pid {parentPid}");

// Leave with the parent, exactly like the original (it holds a SYNCHRONIZE handle on Pro Tools).
try
{
    var parent = Process.GetProcessById(parentPid);
    parent.EnableRaisingEvents = true;
    parent.Exited += (_, _) => { Log.Info("parent exited"); Environment.Exit(0); };
}
catch (ArgumentException)
{
    Log.Error($"parent {parentPid} is not running");
    return 1;
}

FfmpegTools tools;
try
{
    tools = FfmpegTools.Locate(config.FfmpegPath, config.FfprobePath);
    Log.Info($"ffmpeg: {tools.Ffmpeg}; ffprobe: {tools.Ffprobe}");
}
catch (FileNotFoundException e)
{
    // Keep serving so Pro Tools gets clean "unreadable" answers instead of a dead helper.
    Log.Error(e.Message);
    tools = new FfmpegTools("ffmpeg.exe", "ffprobe.exe");
}

try
{
    using var transport = new DipcTransport(parentPid, TimeSpan.FromSeconds(30));
    using var server = new QtServer(tools, config);
    Log.Info("connected");
    while (transport.Receive() is { } request)
    {
        var reply = server.Handle(request);
        if (reply != null)
            transport.Send(reply);
    }
    Log.Info("parent disconnected");
}
catch (Exception e)
{
    Log.Error($"fatal: {e}");
    return 1;
}
return 0;
