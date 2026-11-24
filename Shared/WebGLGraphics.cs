using Blazor.Extensions;
using Blazor.Extensions.Canvas;
using Blazor.Extensions.Canvas.WebGL;
using System;
using System.Threading.Tasks;

class WebGLGraphics
{
    //These must match the attribute names declared in the vertex shader
    private const string _vertexAttribName = "coordinates";
    private const string _normalAttribName = "normal";

    //Vertex shader GLSL source code
    //Passes the position and normal on in view space, where the camera is the origin
    private const string _vertCode = """
        attribute vec3 coordinates;
        attribute vec3 normal;
        uniform mat4 pMatrix;
        uniform mat4 vMatrix;
        uniform mat4 mMatrix;
        varying vec3 vViewPosition;
        varying vec3 vViewNormal;

        void main(void) {
            mat4 mvMatrix = vMatrix * mMatrix;
            vec4 viewPosition = mvMatrix * vec4(coordinates, 1.0);
            vViewPosition = viewPosition.xyz;
            // w = 0 drops the translation. This is only a valid normal transform
            // while the model and view matrices just rotate and translate.
            vViewNormal = (mvMatrix * vec4(normal, 0.0)).xyz;
            gl_Position = pMatrix * viewPosition;
        }
        """;

    //Fragment shader GLSL source code
    //A metal has no colour of its own to show: all that is seen is its surroundings reflected in
    //it, tinted by the metal. So each fragment reflects the view direction off the face and looks
    //that direction up in a made-up studio, which stands in for an environment texture.
    //The /lighting page (Pages/Lighting.razor) draws and tabulates the lights in `studio`,
    //so a change to a light here needs the same change there.
    private const string _fragCode = """
        #ifdef GL_FRAGMENT_PRECISION_HIGH
        precision highp float;
        #else
        precision mediump float;
        #endif
        varying vec3 vViewPosition;
        varying vec3 vViewNormal;

        // A light panel standing on the studio floor: 1 inside it, 0 outside, with soft edges.
        // It is centred on the bearing `centre` and reaches `halfWidth` either side (all in degrees),
        // and its lower edge is at height `bottom` (as the y of the direction).
        float panel(float bearing, float y, float centre, float halfWidth, float softness, float bottom) {
            float offCentre = abs(mod(bearing - centre + 180.0, 360.0) - 180.0);
            float across = 1.0 - smoothstep(halfWidth - softness, halfWidth + softness, offCentre);
            return across * smoothstep(bottom - 0.03, bottom + 0.03, y);
        }

        // The light arriving from each direction of the studio the metal reflects.
        // View space: +x right, +y up, +z towards the camera.
        // The cube spins about y level with the camera, so a face turned by an angle reflects the
        // bearing twice that angle, within about 0.25 of the horizon in y: lights outside that
        // band are never seen.
        vec3 studio(vec3 d) {
            // Compass bearing of the direction: 0 is towards the camera, 90 is to the right
            float bearing = degrees(atan(d.x, d.z));

            // A dim, warm room, its floor darker than its walls
            vec3 light = vec3(1.0, 0.6, 0.35) * mix(0.05, 0.14, smoothstep(-0.22, -0.02, d.y));
            // Key softbox, in front of the cube to the right, brightest at the top
            light += mix(0.5, 0.88, smoothstep(-0.06, 0.25, d.y)) * panel(bearing, d.y, 62.0, 38.0, 6.0, -0.06);
            // Fill softbox, in front of the cube to the left
            light += 0.4 * panel(bearing, d.y, -45.0, 50.0, 8.0, 0.02);
            // Narrow strip light between the two, whose glint sweeps across each face as it turns to the camera
            light += 1.3 * panel(bearing, d.y, 14.0, 2.0, 1.5, -0.3);
            // Another behind the cube, which glints off each face as it turns away
            light += 1.8 * panel(bearing, d.y, 150.0, 4.0, 3.0, -0.3);
            return light;
        }

        void main(void) {
            vec3 n = normalize(vViewNormal);
            vec3 v = normalize(-vViewPosition);
            vec3 r = reflect(-v, n);

            // CSS "gold" (#ffd700), matching the loader in app.css and the icon SVGs, as linear light
            vec3 gold = pow(vec3(1.0, 0.843, 0.0), vec3(2.2));
            // Schlick's Fresnel approximation: reflections take the metal's colour, paling at grazing angles
            vec3 reflectance = mix(gold, vec3(1.0, 0.9, 0.55), pow(1.0 - max(dot(n, v), 0.0), 5.0));

            vec3 colour = reflectance * studio(r);
            // Let overexposed highlights burn out towards white instead of clipping to pure yellow
            colour += max(max(colour.r, colour.g) - 1.0, 0.0);
            gl_FragColor = vec4(pow(min(colour, 1.0), vec3(1.0 / 2.2)), 1.0);
        }
        """;

    private WebGLContext? _webGLContext;
    private BECanvasComponent _canvasReference;
    private float[]? _pMatrix;
    private float[] _vMatrix = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -5, 1 };
    private float[] _mMatrix = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    private WebGLUniformLocation? _pMatrixLoc;
    private WebGLUniformLocation? _vMatrixLoc;
    private WebGLUniformLocation? _mMatrixLoc;
    private int _numIndexes;

    public WebGLGraphics(BECanvasComponent canvasReference)
    {
        _canvasReference = canvasReference;
        //The near plane must be in front of the camera (zMin > 0), or every fragment gets the same depth
        UpdateProjectionMatrix(40, (float)_canvasReference.Width / _canvasReference.Height, 1, 100);
    }

    public async Task Initialise(float[] vertices, float[] normals, ushort[] indexes)
    {
        //Create the WebGL context
        _webGLContext = await _canvasReference.CreateWebGLAsync();

        var shaderProgram = await CreateShaderProgram();

        var vertexBuffer = await CreateVertexBuffer(vertices);
        var normalBuffer = await CreateVertexBuffer(normals);
        var indexBuffer = await CreateIndexBuffer(indexes);

        await AssociateShadersToBuffers(vertexBuffer, normalBuffer, indexBuffer, shaderProgram);

        await GetMatrixLocs(shaderProgram);

        //Enable the depth test
        await _webGLContext.EnableAsync(EnableCap.DEPTH_TEST);

        await _webGLContext.DepthFuncAsync(CompareFunction.LEQUAL);

        //Skip triangles facing away from the camera. WebGL's defaults are what is wanted here:
        //the back is what gets culled, and a counter-clockwise triangle counts as the front.
        await _webGLContext.EnableAsync(EnableCap.CULL_FACE);

        //Set the viewport
        await UpdateViewport((int)_canvasReference.Width, (int)_canvasReference.Height);

        await _webGLContext.ClearColorAsync(0, 0, 0, 1);
        await _webGLContext.ClearDepthAsync(1);

        _numIndexes = indexes.Length;
    }

    public async Task UpdateCanvasColour(float red, float green, float blue, float alpha)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }
        await _webGLContext!.ClearColorAsync(red, green, blue, alpha);
    }

    public async Task UpdateViewport(int width, int height)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }
        await _webGLContext!.ViewportAsync(0, 0, width, height);
    }

    private async Task<WebGLProgram> CreateShaderProgram()
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        //Load the shaders
        var vertShader = await LoadShader(ShaderType.VERTEX_SHADER, _vertCode);
        var fragShader = await LoadShader(ShaderType.FRAGMENT_SHADER, _fragCode);

        //Create the shader program
        var shaderProgram = await _webGLContext!.CreateProgramAsync();
        await _webGLContext.AttachShaderAsync(shaderProgram, vertShader);
        await _webGLContext.AttachShaderAsync(shaderProgram, fragShader);
        await _webGLContext.LinkProgramAsync(shaderProgram);
        await _webGLContext.UseProgramAsync(shaderProgram);

        //Check if creating the shader program failed
        if (!await _webGLContext.GetProgramParameterAsync<bool>(shaderProgram, ProgramParameter.LINK_STATUS))
        {
            throw new Exception("Unable to initialize the shader program: " + await _webGLContext.GetProgramInfoLogAsync(shaderProgram));
        }

        return shaderProgram;
    }

    private async Task<WebGLShader> LoadShader(ShaderType type, string source)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        var shader = await _webGLContext!.CreateShaderAsync(type);   //Create a shader
        await _webGLContext.ShaderSourceAsync(shader, source);      //Load the source code to the shader
        await _webGLContext.CompileShaderAsync(shader);             //Compile the shader

        //Check that the shader compiled successfully
        if (!await _webGLContext.GetShaderParameterAsync<bool>(shader, ShaderParameter.COMPILE_STATUS))
        {
            throw new Exception("An error occurred when compiling the shaders: " + await _webGLContext.GetShaderInfoLogAsync(shader));
        }

        return shader;
    }

    private async Task<WebGLBuffer> CreateVertexBuffer(float[] vertices)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        var vertexBuffer = await _webGLContext!.CreateBufferAsync();                         //Create a buffer for the vectors
        await _webGLContext.BindBufferAsync(BufferType.ARRAY_BUFFER, vertexBuffer);         //Bind an empty array to the buffer
        await _webGLContext.BufferDataAsync(BufferType.ARRAY_BUFFER,                        //Send the vectors to WebGL
                                            vertices,
                                            BufferUsageHint.STATIC_DRAW);
        await _webGLContext.BindBufferAsync(BufferType.ARRAY_BUFFER, null);                 //Unbind the array from the buffer

        return vertexBuffer;
    }

    private async Task<WebGLBuffer> CreateIndexBuffer(ushort[] indexes)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        var indexBuffer = await _webGLContext!.CreateBufferAsync();                          //Create a buffer for the elements
        await _webGLContext.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, indexBuffer);  //Bind an empty array to the buffer
        await _webGLContext.BufferDataAsync(BufferType.ELEMENT_ARRAY_BUFFER,                //Send the elements to WebGL
                                            indexes,
                                            BufferUsageHint.STATIC_DRAW);
        await _webGLContext.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, null);         //Unbind the array from the buffer

        return indexBuffer;
    }

    private async Task AssociateShadersToBuffers(WebGLBuffer vertexBuffer, WebGLBuffer normalBuffer, WebGLBuffer indexBuffer, WebGLProgram shaderProgram)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        await AssociateAttributeToBuffer(vertexBuffer, shaderProgram, _vertexAttribName);
        await AssociateAttributeToBuffer(normalBuffer, shaderProgram, _normalAttribName);

        //Bind the index buffer
        await _webGLContext!.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, indexBuffer);
    }

    //Feeds a vec3 attribute of the shader program from a buffer of floats, three per vertex
    private async Task AssociateAttributeToBuffer(WebGLBuffer buffer, WebGLProgram shaderProgram, string attribName)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        //Bind the buffer object
        await _webGLContext!.BindBufferAsync(BufferType.ARRAY_BUFFER, buffer);

        //Get the attribute location
        var attribLoc = (uint)await _webGLContext.GetAttribLocationAsync(shaderProgram, attribName);

        //Point an attribute to the currently bound vertex buffer object
        await _webGLContext.VertexAttribPointerAsync(attribLoc, 3, DataType.FLOAT, false, 0, 0);

        //Enable the attribute
        await _webGLContext.EnableVertexAttribArrayAsync(attribLoc);
    }

    public async Task Render()
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        await _webGLContext!.UniformMatrixAsync(_pMatrixLoc, false, _pMatrix);
        await _webGLContext.UniformMatrixAsync(_vMatrixLoc, false, _vMatrix);
        await _webGLContext.UniformMatrixAsync(_mMatrixLoc, false, _mMatrix);

        //Clear and draw on the canvas
        await _webGLContext.BeginBatchAsync();
        await _webGLContext.ClearAsync(BufferBits.COLOR_BUFFER_BIT | BufferBits.DEPTH_BUFFER_BIT);
        await _webGLContext.DrawElementsAsync(Primitive.TRIANGLES, _numIndexes, DataType.UNSIGNED_SHORT, 0);
        await _webGLContext.EndBatchAsync();
    }

    private async Task GetMatrixLocs(WebGLProgram shaderProgram)
    {
        if (_webGLContext == null) { throw new Exception("Must initialise WebGLGraphics first"); }

        _pMatrixLoc = await _webGLContext!.GetUniformLocationAsync(shaderProgram, "pMatrix");
        _vMatrixLoc = await _webGLContext.GetUniformLocationAsync(shaderProgram, "vMatrix");
        _mMatrixLoc = await _webGLContext.GetUniformLocationAsync(shaderProgram, "mMatrix");
    }

    public void UpdateProjectionMatrix(float angle, float a, float zMin, float zMax)
    {
        //Prevent divide by 0
        if (zMax <= zMin)
        {
            throw new Exception("Error creating projection matrix: zMax must be greater than zMin.");
        }

        //With zMin at 0 every fragment lands on the far plane, so the depth test cannot tell faces apart
        if (zMin <= 0)
        {
            throw new Exception("Error creating projection matrix: zMin must be greater than 0.");
        }

        //Calculate the matrix
        float ang = MathF.Tan(0.5f * angle * MathF.PI / 180);
        float[] matrix = {  0.5f/ang, 0, 0, 0,
                            0, 0.5f*a/ang, 0, 0,
                            0, 0, -(zMax+zMin)/(zMax-zMin), -1,
                            0, 0, -2*zMax*zMin/(zMax-zMin), 0   };

        _pMatrix = matrix;
    }

    public void RotateX(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv1 = _mMatrix[1];
        float mv5 = _mMatrix[5];
        float mv9 = _mMatrix[9];

        _mMatrix[1] = c * _mMatrix[1] - s * _mMatrix[2];
        _mMatrix[5] = c * _mMatrix[5] - s * _mMatrix[6];
        _mMatrix[9] = c * _mMatrix[9] - s * _mMatrix[10];
        _mMatrix[2] = c * _mMatrix[2] + s * mv1;
        _mMatrix[6] = c * _mMatrix[6] + s * mv5;
        _mMatrix[10] = c * _mMatrix[10] + s * mv9;
    }

    public void RotateY(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv0 = _mMatrix[0];
        float mv4 = _mMatrix[4];
        float mv8 = _mMatrix[8];

        _mMatrix[0] = c * _mMatrix[0] + s * _mMatrix[2];
        _mMatrix[4] = c * _mMatrix[4] + s * _mMatrix[6];
        _mMatrix[8] = c * _mMatrix[8] + s * _mMatrix[10];
        _mMatrix[2] = c * _mMatrix[2] - s * mv0;
        _mMatrix[6] = c * _mMatrix[6] - s * mv4;
        _mMatrix[10] = c * _mMatrix[10] - s * mv8;
    }

    public void RotateZ(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv0 = _mMatrix[0];
        float mv4 = _mMatrix[4];
        float mv8 = _mMatrix[8];

        _mMatrix[0] = c * _mMatrix[0] - s * _mMatrix[1];
        _mMatrix[4] = c * _mMatrix[4] - s * _mMatrix[5];
        _mMatrix[8] = c * _mMatrix[8] - s * _mMatrix[9];
        _mMatrix[1] = c * _mMatrix[1] + s * mv0;
        _mMatrix[5] = c * _mMatrix[5] + s * mv4;
        _mMatrix[9] = c * _mMatrix[9] + s * mv8;
    }
}