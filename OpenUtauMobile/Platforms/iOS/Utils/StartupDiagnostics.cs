using System.Runtime.InteropServices;
using Foundation;

namespace OpenUtauMobile.Platforms.iOS.Utils
{
    /// <summary>
    /// Writes one line per native dependency to stdout at launch, e.g.
    /// <c>[ios-port] worldline: OK</c>. Cheap, and lets the CI simulator smoke
    /// test (and anyone reading device logs) confirm the native pieces that
    /// differ from Android actually load.
    /// </summary>
    public static class StartupDiagnostics
    {
        private static readonly string[] WorldlineExports =
            ["F0", "DecodeMgc", "DecodeBap", "WorldSynthesis", "Resample", "PhraseSynthNew",
             "PhraseSynthDelete", "PhraseSynthAddRequest", "PhraseSynthSetCurves", "PhraseSynthSynth"];

        public static void Run()
        {
            Report("worldline", CheckWorldline);
            // Goes through [DllImport("worldline")] + NativeLibraryResolver and runs real DSP.
            Report("worldline-pinvoke", () =>
            {
                double[]? f0 = OpenUtau.Core.Render.Worldline.F0(new float[44100], 44100, 10.0, 0);
                return f0 != null ? $"OK F0 -> {f0.Length} frames" : "FAIL F0 returned null";
            });
            Report("onnxruntime", () => "OK " + Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString());
        }

        private static string CheckWorldline()
        {
            string path = Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath ?? "", "worldline.framework", "worldline");
            if (!NativeLibrary.TryLoad(path, out IntPtr handle))
            {
                return "FAIL cannot load " + path;
            }
            var missing = WorldlineExports.Where(e => !NativeLibrary.TryGetExport(handle, e, out _)).ToList();
            return missing.Count == 0 ? "OK" : "FAIL missing exports: " + string.Join(", ", missing);
        }

        private static void Report(string name, Func<string> check)
        {
            string result;
            try
            {
                result = check();
            }
            catch (Exception e)
            {
                result = "FAIL " + e.GetType().Name + ": " + e.Message;
            }
            Console.WriteLine($"[ios-port] {name}: {result}");
        }
    }
}
