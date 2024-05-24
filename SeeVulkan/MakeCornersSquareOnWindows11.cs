public static class MakeCornersSquareOnWindows11
{
    public static void MakeCornersSquare(this IWindow window)
    {
        if (!window.Native.Win32.HasValue)
            return;

        var (hwnd, _, _) = window.Native.Win32.Value;
        var attribute = DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE;
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, attribute, ref preference, sizeof(uint));
    }

    enum DWMWINDOWATTRIBUTE
    {
        DWMWA_WINDOW_CORNER_PREFERENCE = 33
    }

    enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT      = 0,
        DWMWCP_DONOTROUND   = 1,
        DWMWCP_ROUND        = 2,
        DWMWCP_ROUNDSMALL   = 3
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void DwmSetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE attribute,
        ref DWM_WINDOW_CORNER_PREFERENCE pvAttribute, uint cbAttribute);
}