
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Input;
using System.Drawing;

class ImGuiWindow
{
    public static void Run()
    {
        using var window = Window.Create(WindowOptions.Default);

        ImGuiController controller = null;
        GL gl = null;
        IInputContext inputContext = null;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();
            controller = new ImGuiController(gl, window, inputContext);
        };

        window.FramebufferResize += s => gl.Viewport(s);

        window.Render += delta =>
        {
            controller.Update((float)delta);

            gl.ClearColor(Color.FromArgb(255, (int)(.45f * 255), (int)(.55f * 255), (int)(.60f * 255)));
            gl.Clear((uint)ClearBufferMask.ColorBufferBit);

            ImGuiNET.ImGui.ShowDemoWindow();

            controller.Render();
        };

        window.Closing += () =>
        {
            controller?.Dispose();
            inputContext?.Dispose();
            gl?.Dispose();
        };

        window.Run();
        window.Dispose();
    }
}