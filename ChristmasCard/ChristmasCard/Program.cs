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
        private int _transformUBO;
        private Matrix4[] _worldMatrices;
        private Matrix4 _viewMatrix;
        private float _zoom = 1.0f;
        private const int MAX_INSTANCES = 10;

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
                if (id >= 3) return;

                float size = 0.5;
                float height = size * sqrt(3.0);

                vec2 position;
                switch (id) {
                    case 0:
                        position = vec2(-size, -height/2);
                        break;
                    case 1:
                        position = vec2(size, -height/2);
                        break;
                    case 2:
                        position = vec2(0, height/2);
                        break;
                }

                vertices[id].Position = position;
            }
        ";

        private readonly string _vertexShaderSource = @"
            #version 430
            layout (location = 0) in vec2 aPosition;
            
            layout(std140, binding = 1) uniform TransformUBO {
                mat4 viewMatrix;
                mat4 worldMatrices[10]; // Match MAX_INSTANCES
            };
            
            out vec2 FragPos;
            flat out int instanceID;
            
            void main() {
                instanceID = gl_InstanceID;
                vec4 worldPosition = worldMatrices[gl_InstanceID] * vec4(aPosition, 0.0, 1.0);
                vec4 viewPosition = viewMatrix * worldPosition;
                FragPos = aPosition; // Keep local coordinates for circle calculation
                gl_Position = viewPosition;
            }
        ";

        private readonly string _fragmentShaderSource = @"
            #version 430
            out vec4 FragColor;
            
            in vec2 FragPos;
            flat in int instanceID;

            void main() {
                float size = 0.5;
                float height = size * sqrt(3.0);
                vec2 center = vec2(0.0, -height/6.0);
                float radius = size / sqrt(3.0);
                
                if (length(FragPos - center) <= radius) {
                    // Create different colors based on instanceID
                    vec3 colors[3] = vec3[](
                        vec3(1.0, 0.0, 0.0), // Red
                        vec3(0.0, 1.0, 0.0), // Green
                        vec3(0.0, 0.0, 1.0)  // Blue
                    );
                    vec3 color = colors[instanceID % 3];
                    FragColor = vec4(color, 1.0);
                } else {
                    FragColor = vec4(0.2, 0.2, 0.2, 1.0);
                }
            }
        ";

        public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
            _worldMatrices = new Matrix4[MAX_INSTANCES];
            _viewMatrix = Matrix4.Identity;
            InitializeWorldMatrices();
        }

        private void InitializeWorldMatrices()
        {
            // Initialize matrices for different positions
            for (int i = 0; i < MAX_INSTANCES; i++)
            {
                float x = (i % 3) * 1.5f - 1.5f; // Arrange in a grid
                float y = (i / 3) * 1.5f - 0.75f;
                _worldMatrices[i] = Matrix4.CreateTranslation(x, y, 0);
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            // Adjust zoom based on scroll direction
            float zoomSpeed = 0.1f;
            _zoom += e.OffsetY * zoomSpeed;
            _zoom = Math.Max(0.1f, _zoom); // Prevent negative or zero zoom

            // Update view matrix with new zoom
            _viewMatrix = Matrix4.CreateScale(_zoom);
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

            // Create and initialize transform UBO
            _transformUBO = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, _transformUBO);
            GL.BufferData(BufferTarget.UniformBuffer, (1 + MAX_INSTANCES) * sizeof(float) * 16, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 1, _transformUBO);

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

            // Update transform UBO with current matrices
            GL.BindBuffer(BufferTarget.UniformBuffer, _transformUBO);
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, sizeof(float) * 16, ref _viewMatrix);
            GL.BufferSubData(BufferTarget.UniformBuffer, new IntPtr(sizeof(float) * 16), MAX_INSTANCES * sizeof(float) * 16, _worldMatrices);

            // Draw multiple instances
            GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 3, MAX_INSTANCES);

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
            GL.DeleteBuffer(_transformUBO);
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
                ClientSize = new Vector2i(800, 800),
                Title = "Kerstkaart 2024 - Harm Cox",
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