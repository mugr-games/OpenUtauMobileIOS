using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;

namespace OpenUtauMobile.Platforms.iOS.Utils
{
    /// <summary>
    /// OpenUtau.Core P/Invokes <c>[DllImport("worldline")]</c>. On Android that
    /// resolves to libworldline.so; on iOS the library ships as an embedded
    /// dynamic framework (Frameworks/worldline.framework/worldline, built by
    /// native/ios/build_worldline_ios.sh), which the default probing does not
    /// find by the bare name. This maps the name to the framework binary.
    /// </summary>
    public static class NativeLibraryResolver
    {
        private static IntPtr _worldline;

        public static void Register()
        {
            NativeLibrary.SetDllImportResolver(typeof(OpenUtau.Core.Render.Worldline).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "worldline" && libraryName != "libworldline")
            {
                return IntPtr.Zero; // Default resolution for everything else.
            }
            if (_worldline != IntPtr.Zero)
            {
                return _worldline;
            }
            string[] candidates =
            [
                Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath ?? "", "worldline.framework", "worldline"),
                "@rpath/worldline.framework/worldline",
            ];
            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    _worldline = handle;
                    return handle;
                }
            }
            return IntPtr.Zero;
        }
    }
}
