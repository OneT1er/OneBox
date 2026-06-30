using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;

namespace PowerAudioManager
{
    public class PowerPlanInfo
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    public class AudioDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public bool IsHidden { get; set; }
        public int HotkeyIndex { get; set; } // 1..9 映射到 Ctrl+Alt+数字键；0 表示未设置
    }

}
