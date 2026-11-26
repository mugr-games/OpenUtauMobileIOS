using CommunityToolkit.Maui.Alerts;
using OpenUtauMobile.Resources.Strings;
using Serilog;

namespace OpenUtauMobile.Platforms.iOS.Utils
{
    /// <summary>
    /// The in-app file browser (FilePickerPopup) walks the real filesystem,
    /// which on iOS is only the app sandbox. To open files from iCloud Drive,
    /// other apps or "On My iPhone", iOS uses the system document picker
    /// instead, and the picked file is copied into the sandbox so the rest of
    /// the app can keep working with plain paths (autosave next to projects,
    /// re-opening recent files, etc.).
    ///
    ///  * Projects, MIDI and audio go to Documents/Imported, which the Files app
    ///    shows under "On My iPhone / OpenUtau Mobile" (UIFileSharingEnabled).
    ///  * Singer / dependency archives are only needed for installation, so
    ///    they go to Caches/Imported, which iOS may purge.
    /// </summary>
    public static class IosFileImporter
    {
        private static readonly string[] ArchiveTypes = [".zip", ".rar", ".uar", ".vogeon", ".oudep", ".7z"];

        public static string DocumentsPath =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        public static async Task<string> PickAndImportAsync(string[] types)
        {
            FileResult? result;
            try
            {
                result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        // Extensions like .ustx/.vogeon have no registered UTI, so
                        // accept any item and check the extension afterwards.
                        { DevicePlatform.iOS, ["public.item"] },
                    }),
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "iOS document picker failed");
                return string.Empty;
            }
            if (result == null)
            {
                return string.Empty; // Cancelled.
            }

            string fileName = result.FileName;
            if (!types.Contains("*") && !types.Any(t => fileName.EndsWith(t, StringComparison.OrdinalIgnoreCase)))
            {
                await Toast.Make(string.Format(AppResources.WrongFileTypeToast, string.Join("，*", types)),
                    CommunityToolkit.Maui.Core.ToastDuration.Short, 16).Show();
                return string.Empty;
            }

            // Already inside our own Documents folder (picked via "On My iPhone"):
            // use it in place so edits land in the original file.
            string? fullPath = result.FullPath;
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath) && IsUnder(fullPath, DocumentsPath))
            {
                return fullPath;
            }

            bool isArchive = ArchiveTypes.Any(t => fileName.EndsWith(t, StringComparison.OrdinalIgnoreCase));
            string targetDir = isArchive
                ? Path.Combine(FileSystem.CacheDirectory, "Imported")
                : Path.Combine(DocumentsPath, "Imported");
            Directory.CreateDirectory(targetDir);
            string targetPath = isArchive
                ? Path.Combine(targetDir, fileName)
                : UniquePath(targetDir, fileName);

            try
            {
                await using Stream source = await result.OpenReadAsync();
                await using FileStream target = File.Create(targetPath);
                await source.CopyToAsync(target);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to import {fileName}");
                TryDelete(targetPath);
                await Toast.Make(string.Format(AppResources.Error, ex.Message),
                    CommunityToolkit.Maui.Core.ToastDuration.Long, 16).Show();
                return string.Empty;
            }
            Log.Information($"Imported {fileName} to {targetPath}");
            return targetPath;
        }

        private static bool IsUnder(string path, string directory)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // /private/var and /var are the same location on iOS.
            static string Normalize(string p) => p.StartsWith("/private/") ? p["/private".Length..] : p;
            return Normalize(full).StartsWith(Normalize(dir), StringComparison.Ordinal);
        }

        /// <summary>Never overwrite an earlier import: the user may have edited it.</summary>
        private static string UniquePath(string dir, string fileName)
        {
            string candidate = Path.Combine(dir, fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 1; File.Exists(candidate); i++)
            {
                candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            }
            return candidate;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
