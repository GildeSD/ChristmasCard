using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System.Runtime.InteropServices;

namespace ComputeTriangle
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public Vector2 Position;
    }

    public class Game : GameWindow
    {
        private int _computeProgram;
        private int _renderProgram;
        private int _vertexBuffer;
        private int _vertexArrayObject;

        private readonly string _computeShaderSource = @"
            #version 430
            
            struct Vertex {
                vec2 Position;
            };

            layout(std430, binding = 0) buffer VertexBuffer {
                Vertex vertices[];
            };

            layout(local_size_x = 3, local_size_y = 1, local_size_z = 1) in;

            void main() {
                uint id = gl_GlobalInvocationID.x;
                if (id >= 3) return; // We only need 3 vertices

                // Create an equilateral triangle
                float angle = radians(120.0) * id;
                float radius = 0.5;
                vec2 position = vec2(
                    cos(angle) * radius,
                    sin(angle) * radius
                );

                vertices[id].Position = position;
            }
        ";

        private readonly string _vertexShaderSource = @"
            #version 430
            layout (location = 0) in vec2 aPosition;
            
            void main() {
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }
        ";

        private readonly string _fragmentShaderSource = @"
            #version 430
            out vec4 FragColor;
            
            void main() {
                FragColor = vec4(1.0, 0.0, 0.0, 1.0); // Red triangle
            }
        ";

        public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
        }

        protected override void OnLoad()
        {
            base.OnLoad();

            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);

            // Create compute shader
            _computeProgram = CreateShaderProgram(_computeShaderSource, ShaderType.ComputeShader);

            // Create render shaders
            _renderProgram = CreateShaderProgram(
                (_vertexShaderSource, ShaderType.VertexShader),
                (_fragmentShaderSource, ShaderType.FragmentShader)
            );

            // Create vertex buffer
            _vertexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 3 * Marshal.SizeOf<Vertex>(), IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _vertexBuffer);

            // Set up VAO
            _vertexArrayObject = GL.GenVertexArray();
            GL.BindVertexArray(_vertexArrayObject);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Marshal.SizeOf<Vertex>(), 0);
            GL.EnableVertexAttribArray(0);

            // Run compute shader once to generate the triangle
            GL.UseProgram(_computeProgram);
            GL.DispatchCompute(1, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);

            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.UseProgram(_renderProgram);
            GL.BindVertexArray(_vertexArrayObject);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            SwapBuffers();
        }

        private int CreateShaderProgram(string source, ShaderType type)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            string infoLog = GL.GetShaderInfoLog(shader);
            if (!string.IsNullOrWhiteSpace(infoLog))
            {
                throw new Exception($"Error compiling shader: {infoLog}");
            }

            int program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.DeleteShader(shader);

            infoLog = GL.GetProgramInfoLog(program);
            if (!string.IsNullOrWhiteSpace(infoLog))
            {
                throw new Exception($"Error linking program: {infoLog}");
            }

            return program;
        }

        private int CreateShaderProgram(params (string source, ShaderType type)[] shaders)
        {
            int program = GL.CreateProgram();

            foreach (var (source, type) in shaders)
            {
                int shader = GL.CreateShader(type);
                GL.ShaderSource(shader, source);
                GL.CompileShader(shader);

                string infoLog = GL.GetShaderInfoLog(shader);
                if (!string.IsNullOrWhiteSpace(infoLog))
                {
                    throw new Exception($"Error compiling shader: {infoLog}");
                }

                GL.AttachShader(program, shader);
                GL.DeleteShader(shader);
            }

            GL.LinkProgram(program);

            string programInfoLog = GL.GetProgramInfoLog(program);
            if (!string.IsNullOrWhiteSpace(programInfoLog))
            {
                throw new Exception($"Error linking program: {programInfoLog}");
            }

            return program;
        }

        protected override void OnUnload()
        {
            GL.DeleteProgram(_computeProgram);
            GL.DeleteProgram(_renderProgram);
            GL.DeleteBuffer(_vertexBuffer);
            GL.DeleteVertexArray(_vertexArrayObject);
            base.OnUnload();
        }
    }

    public class Program
    {
        public static void Main()
        {
            var nativeWindowSettings = new NativeWindowSettings()
            {
                Size = new Vector2i(800, 600),
                Title = "OpenTK Compute Triangle",
                Flags = ContextFlags.ForwardCompatible,
                APIVersion = new Version(4, 3)
            };

            using (var game = new Game(GameWindowSettings.Default, nativeWindowSettings))
            {
                game.Run();
            }
        }
    }
}