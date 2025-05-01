using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Shell;

namespace MediaIsland.Helpers
{
    public static class IconHelper
    {
        public static Icon? GetAppIcon(string userModelId)
        {
            try
            {
                // 优先处理UWP应用
                if (userModelId.Contains("!") && userModelId.Contains("_"))
                {
                    try
                    {
                        var shellObj = ShellObject.FromParsingName($"shell:appsFolder\\{userModelId}");
                        if (shellObj?.Thumbnail != null)
                        {
                            return shellObj.Thumbnail.LargeIcon;
                        }
                    }
                    catch
                    {
                        // 备用UWP图标获取方式
                        string? packageName = userModelId.Split('!')[0];
                        string? manifestPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            "WindowsApps",
                            packageName,
                            "AppxManifest.xml");

                        if (File.Exists(manifestPath))
                        {
                            var xmlDoc = new System.Xml.XmlDocument();
                            xmlDoc.Load(manifestPath);
                            var logoNode = xmlDoc.SelectSingleNode("//*[local-name()='Logo']");
                            if (logoNode != null)
                            {
                                string logoPath = Path.Combine(
                                    Path.GetDirectoryName(manifestPath),
                                    logoNode.InnerText);
                                return new Icon(logoPath);
                            }
                        }
                    }
                }

                // Win32应用处理
                string processName = userModelId.Split('!').Last()
                    .Replace(".exe", "")
                    .Replace("_", "");

                // 通过进程获取
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    string exePath = processes[0].MainModule.FileName;
                    return Icon.ExtractAssociatedIcon(exePath);
                }

                // 通过常见安装路径搜索
                string[] searchPaths = {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"C:\Program Files\WindowsApps\"
                };

                foreach (var path in searchPaths)
                {
                    string possibleExe = Path.Combine(path, $"{processName}.exe");
                    if (File.Exists(possibleExe))
                    {
                        return Icon.ExtractAssociatedIcon(possibleExe);
                    }

                    // 处理带空格的文件名
                    possibleExe = Path.Combine(path, $"{processName}*.exe");
                    var files = Directory.GetFiles(path, $"{processName}*.exe");
                    if (files.Length > 0)
                    {
                        return Icon.ExtractAssociatedIcon(files[0]);
                    }
                }

                // 最终回退方案
                return SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        public static ImageSource IconToImageSourceConverter(Icon icon)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    icon.Save(ms);
                    ms.Seek(0, SeekOrigin.Begin);
                    return BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                }
            }
            catch (ArgumentNullException)
            {
                throw new ArgumentNullException(nameof(icon));
            }
        }
    }
}