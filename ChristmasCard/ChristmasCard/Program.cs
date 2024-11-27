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

    [StructLayout(LayoutKind.Sequential)]
    struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public float Size;
    }

    public class Game : GameWindow
    {
        private int _computeProgram;
        private int _renderProgram;
        private int _vertexBuffer;
        private int _vertexArrayObject;
        private int _transformUBO;
        private Matrix4 _worldMatrix;
        private Matrix4 _viewMatrix;
        private Matrix4 _projectionMatrix;
        private float _zoom = 1.0f;
        private const int MAX_INSTANCES = 10;

        // Firework properties
        private Vector2 _position;
        private Vector2 _velocity;
        private const float GRAVITY = -2.0f;
        private const float INITIAL_VELOCITY = 3.0f;
        private bool _isLaunched = false;
        private bool _hasExploded = false;
        private List<Particle> _particles = new List<Particle>();
        private Random _random = new Random();

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

                float size = 0.1; // Smaller triangle
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
                mat4 projectionMatrix;
                mat4 viewMatrix;
                mat4 worldMatrix;
            };
            
            out vec2 FragPos;
            
            void main() {
                vec4 worldPosition = worldMatrix * vec4(aPosition, 0.0, 1.0);
                vec4 viewPosition = viewMatrix * worldPosition;
                vec4 clipPosition = projectionMatrix * viewPosition;
                FragPos = aPosition;
                gl_Position = clipPosition;
            }
        ";

        private readonly string _fragmentShaderSource = @"
            #version 430
            out vec4 FragColor;
            
            in vec2 FragPos;

            void main() {
                float size = 0.1;
                float height = size * sqrt(3.0);
                vec2 center = vec2(0.0, -height/6.0);
                float radius = size / sqrt(3.0);
                
                if (length(FragPos - center) <= radius) {
                    FragColor = vec4(1.0, 0.5, 0.0, 1.0); // Orange color for the firework
                } else {
                    discard;
                }
            }
        ";

        public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
            _worldMatrix = Matrix4.Identity;
            _viewMatrix = Matrix4.Identity;
            _projectionMatrix = Matrix4.Identity;

            // Initialize firework position at bottom center
            _position = new Vector2(0.0f, -0.9f);
            _velocity = new Vector2(0.0f, INITIAL_VELOCITY);
            _isLaunched = true;
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

        private void UpdateProjectionMatrix()
        {
            float aspectRatio = (float)Size.X / Size.Y;
            _projectionMatrix = Matrix4.CreateOrthographic(2f * aspectRatio, 2f, -1f, 1f);
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            base.OnUpdateFrame(args);

            if (_isLaunched)
            {
                // Update velocity and position
                _velocity.Y += GRAVITY * (float)args.Time;
                _position += _velocity * (float)args.Time;

                // Reset if the firework goes off screen
                if (_position.Y < -1.0f)
                {
                    _position = new Vector2(0.0f, -0.9f);
                    _velocity = new Vector2(0.0f, INITIAL_VELOCITY);
                }

                _worldMatrix = Matrix4.CreateTranslation(new Vector3(_position.X, _position.Y, 0));
            }
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            GL.Viewport(0, 0, Size.X, Size.Y);
            UpdateProjectionMatrix();
        }

        protected override void OnLoad()
        {
            base.OnLoad();

            GL.ClearColor(0.0f, 0.0f, 0.1f, 1.0f);
            UpdateProjectionMatrix();

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
            GL.BufferData(BufferTarget.UniformBuffer, 3 * sizeof(float) * 16, IntPtr.Zero, BufferUsageHint.DynamicDraw);
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
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, sizeof(float) * 16, ref _projectionMatrix);
            GL.BufferSubData(BufferTarget.UniformBuffer, new IntPtr(sizeof(float) * 16), sizeof(float) * 16, ref _viewMatrix);
            GL.BufferSubData(BufferTarget.UniformBuffer, new IntPtr(sizeof(float) * 32), sizeof(float) * 16, ref _worldMatrix);

            // Draw multiple instances
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