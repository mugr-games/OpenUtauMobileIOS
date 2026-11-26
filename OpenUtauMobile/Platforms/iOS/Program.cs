using ObjCRuntime;
using UIKit;

namespace OpenUtauMobile
{
    public class Program
    {
        // This is the main entry point of the application.
        static void Main(string[] args)
        {
            // Map [DllImport("worldline")] to the embedded worldline.framework
            // before anything in OpenUtau.Core can touch it.
            OpenUtauMobile.Platforms.iOS.Utils.NativeLibraryResolver.Register();
            OpenUtauMobile.Platforms.iOS.Utils.StartupDiagnostics.Run();

            // if you want to use a different Application Delegate class from "AppDelegate"
            // you can specify it here.
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
    }
}
