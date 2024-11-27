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
        private int _particleBuffer;
        private int _vertexArrayObject;
        private int _transformUBO;
        private Matrix4 _worldMatrix;
        private Matrix4 _viewMatrix;
        private Matrix4 _projectionMatrix;
        private float _zoom = 1.0f;
        private const int MAX_PARTICLES = 50;

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
            
            struct Particle {
                vec2 Position;
                vec2 Velocity;
                float Life;
                float Size;
            };

            layout(std430, binding = 0) buffer ParticleBuffer {
                Particle particles[];
            };

            layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

            void main() {
                uint id = gl_GlobalInvocationID.x;
                if (id >= particles.length()) return;

                float size = particles[id].Size;
                particles[id].Position = particles[id].Position + particles[id].Velocity;
                particles[id].Velocity.y += -1.0 * 0.016; // Apply gravity
                particles[id].Life -= 0.016; // Decrease life
            }
        ";

        private readonly string _vertexShaderSource = @"
            #version 430

            struct Particle {
                vec2 Position;
                vec2 Velocity;
                float Life;
                float Size;
            };

            layout(std430, binding = 0) buffer ParticleBuffer {
                Particle particles[];
            };

            layout(std140, binding = 1) uniform TransformUBO {
                mat4 projectionMatrix;
                mat4 viewMatrix;
                mat4 worldMatrix;
            };

            out vec2 FragPos;
            out float ParticleLife;
            out float ParticleSize;

            void main() {
                Particle particle = particles[gl_InstanceID];
    
                // Make the triangle larger to accommodate the glow
                float size = particle.Size * 2.0; // Double the size to allow for glow
                float height = size * sqrt(3.0);
    
                // Generate triangle vertices with consistent size scaling
                vec2 position;
                vec2 localPos;
                switch (gl_VertexID) {
                    case 0:
                        localPos = vec2(-1.0, -1.0/sqrt(3.0));
                        position = particle.Position + localPos * size;
                        break;
                    case 1:
                        localPos = vec2(1.0, -1.0/sqrt(3.0));
                        position = particle.Position + localPos * size;
                        break;
                    case 2:
                        localPos = vec2(0.0, 2.0/sqrt(3.0));
                        position = particle.Position + localPos * size;
                        break;
                }
    
                vec4 worldPosition = worldMatrix * vec4(position, 0.0, 1.0);
                vec4 viewPosition = viewMatrix * worldPosition;
                vec4 clipPosition = projectionMatrix * viewPosition;
    
                // Pass the local position for glow calculation
                FragPos = localPos;
                ParticleLife = particle.Life;
                ParticleSize = particle.Size;
                gl_Position = clipPosition;
            }
        ";

        private readonly string _fragmentShaderSource = @"
            #version 430
            out vec4 FragColor;

            in vec2 FragPos;
            in float ParticleLife;
            in float ParticleSize;

            void main() {
                float radius = length(FragPos);
    
                // Adjust the radius calculation to create a smoother falloff
                float normalizedRadius = radius * 2; // Scale down the radius to make the glow extend further
    
                // Create a softer falloff for the glow
                float alpha = clamp(1.0 - (normalizedRadius * normalizedRadius), 0.0, 1.0) * ParticleLife;
    
                // Create a colorful particle effect that scales with size
                vec3 baseColor = mix(vec3(1.0, 0.3, 0.0), vec3(1.0, 0.8, 0.0), ParticleLife);
    
                // Enhanced glow effect with smoother falloff
                float glowIntensity = exp(-normalizedRadius * 2.0); // Exponential falloff for more natural glow
                vec3 color = mix(baseColor, vec3(1.0, 1.0, 1.0), glowIntensity * 0.5);
    
                // Adjust intensity based on particle size
                float sizeCompensation = 1.0 / (ParticleSize * 20.0);
                color *= (1.0 + sizeCompensation);
    
                // Additional intensity boost for the core
                float coreGlow = exp(-radius * 8.0);
                color += vec3(1.0, 1.0, 0.8) * coreGlow * ParticleLife;
    
                FragColor = vec4(color, alpha);
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

        private void CreateExplosion()
        {
            _particles.Clear();
            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                float angle = (float)(_random.NextDouble() * Math.PI * 2);
                float speed = (float)(_random.NextDouble() * 0.8f + 0.4f);

                Vector2 offset = new Vector2(
                    (float)(_random.NextDouble() - 0.5) * 0.1f,
                    (float)(_random.NextDouble() - 0.5) * 0.1f
                );

                Vector2 velocity = new Vector2(
                    (float)Math.Cos(angle) * speed,
                    (float)Math.Sin(angle) * speed
                );

                _particles.Add(new Particle
                {
                    Position = _position + offset,
                    Velocity = velocity,
                    Life = 1.0f + (float)_random.NextDouble() * 0.5f,
                    Size = 0.02f + (float)_random.NextDouble() * 0.01f
                });
            }

            Console.WriteLine($"Created {_particles.Count} particles");
            UpdateParticleBuffer();
        }

        private void UpdateParticleBuffer()
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _particleBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer,
                _particles.Count * Marshal.SizeOf<Particle>(),
                _particles.ToArray(),
                BufferUsageHint.DynamicDraw);
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

            if (_isLaunched && !_hasExploded)
            {
                // Update velocity and position
                _velocity.Y += GRAVITY * (float)args.Time;
                _position += _velocity * (float)args.Time;

                // Check if firework reached highest point
                if (_velocity.Y <= 0)
                {
                    Console.WriteLine("Explosion triggered!"); // Debug print
                    _hasExploded = true;
                    _isLaunched = false;  // Add this line
                    CreateExplosion();
                }
            }
            else if (_hasExploded && _particles.Count > 0)  // Modified condition
            {
                // Update particles
                for (int i = 0; i < _particles.Count; i++)
                {
                    var particle = _particles[i];
                    particle.Position += particle.Velocity * (float)args.Time;
                    particle.Velocity.Y += GRAVITY * (float)args.Time;
                    particle.Life -= 0.5f * (float)args.Time;  // Slower life decrease
                    _particles[i] = particle;
                }

                // Remove dead particles
                _particles.RemoveAll(p => p.Life <= 0);

                // Update buffer with new particle data
                if (_particles.Count > 0)
                {
                    UpdateParticleBuffer();
                }

                // Reset if all particles are dead
                if (_particles.Count == 0)
                {
                    _hasExploded = false;
                    _isLaunched = true;
                    _position = new Vector2(0.0f, -0.9f);
                    _velocity = new Vector2(0.0f, INITIAL_VELOCITY);
                }
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
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            UpdateProjectionMatrix();

            // Create compute shader
            _computeProgram = CreateShaderProgram(_computeShaderSource, ShaderType.ComputeShader);

            // Create render shaders
            _renderProgram = CreateShaderProgram(
                (_vertexShaderSource, ShaderType.VertexShader),
                (_fragmentShaderSource, ShaderType.FragmentShader)
            );

            // Create particle buffer
            _particleBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _particleBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer,
                MAX_PARTICLES * Marshal.SizeOf<Particle>(),
                IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _particleBuffer);

            // Create transform UBO
            _transformUBO = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, _transformUBO);
            GL.BufferData(BufferTarget.UniformBuffer, 3 * sizeof(float) * 16,
                IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 1, _transformUBO);

            // Set up VAO
            _vertexArrayObject = GL.GenVertexArray();
            GL.BindVertexArray(_vertexArrayObject);
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);

            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.UseProgram(_renderProgram);
            GL.BindVertexArray(_vertexArrayObject);

            // Update transform UBO
            GL.BindBuffer(BufferTarget.UniformBuffer, _transformUBO);
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero,
                sizeof(float) * 16, ref _projectionMatrix);
            GL.BufferSubData(BufferTarget.UniformBuffer,
                new IntPtr(sizeof(float) * 16), sizeof(float) * 16, ref _viewMatrix);
            GL.BufferSubData(BufferTarget.UniformBuffer,
                new IntPtr(sizeof(float) * 32), sizeof(float) * 16, ref _worldMatrix);

            if (!_hasExploded)
            {
                // Draw single firework
                Particle mainFirework = new Particle
                {
                    Position = _position,
                    Velocity = Vector2.Zero,
                    Life = 1.0f,
                    Size = 0.05f
                };

                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _particleBuffer);
                GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                    Marshal.SizeOf<Particle>(), ref mainFirework);
                GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 3, 1);
            }
            else if (_particles.Count > 0)  // Only draw if we have particles
            {
                // Draw particles
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _particleBuffer);
                GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 3, _particles.Count);
            }

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