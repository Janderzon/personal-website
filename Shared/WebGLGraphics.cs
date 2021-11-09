using Blazor.Extensions; 
using Blazor.Extensions.Canvas;
using Blazor.Extensions.Canvas.WebGL;
using System;
using System.Threading.Tasks;

class WebGLGraphics{
    private const string _vertexAttribName = "coordinates";
    //Vertex shader GLSL source code
    private const string _vertCode =    "attribute vec3 " + _vertexAttribName + ";" +
                                        "uniform mat4 pMatrix;" + 
                                        "uniform mat4 vMatrix;" +
                                        "uniform mat4 mMatrix;" +
                                        "void main(void) {" + 
                                            "gl_Position = pMatrix*vMatrix*mMatrix*vec4(coordinates, 1.0);" + 
                                        "}";
    //Fragment shader GLSL source code
    private const string _fragCode = "void main(void) {gl_FragColor = vec4(0.0, 0.86, 0.89, 1.0);}";

    private WebGLContext? _webGLContext;
    private BECanvasComponent _canvasReference;
    private float[]? _pMatrix;
    private float[] _vMatrix = {1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,-5,1};
    private float[] _mMatrix = {1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
    private WebGLUniformLocation? _pMatrixLoc;
    private WebGLUniformLocation? _vMatrixLoc;
    private WebGLUniformLocation? _mMatrixLoc;
    private int _numIndexes;

    public WebGLGraphics(BECanvasComponent canvasReference){
        _canvasReference = canvasReference;
        UpdateProjectionMatrix(40, (float)_canvasReference.Width/_canvasReference.Height, 0, 100);
    }

    public async Task Initialise(float[] vertices, ushort[] indexes){
        //Create the WebGL context
        _webGLContext = await _canvasReference.CreateWebGLAsync();

        var shaderProgram = await CreateShaderProgram();

        var vertexBuffer = await CreateVertexBuffer(vertices);
        var indexBuffer = await CreateIndexBuffer(indexes);

        await AssociateShadersToBuffers(vertexBuffer, indexBuffer, shaderProgram);

        await GetMatrixLocs(shaderProgram);

        //Enable the depth test
        await _webGLContext.EnableAsync(EnableCap.DEPTH_TEST);

        await _webGLContext.DepthFuncAsync(CompareFunction.LEQUAL);

        //Set the viewport
        await _webGLContext.ViewportAsync(0, 0, (int)_canvasReference.Width, (int)_canvasReference.Height);

        await _webGLContext.ClearColorAsync(0, 0, 0, 1);
        await _webGLContext.ClearDepthAsync(1); 

        _numIndexes = indexes.Length;
    }

    public async Task UpdateCanvasColour(float red, float green, float blue, float alpha){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}
        await _webGLContext!.ClearColorAsync(red, green, blue, alpha);
    }

    private async Task<WebGLProgram> CreateShaderProgram(){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

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
        if(!await _webGLContext.GetProgramParameterAsync<bool>(shaderProgram, ProgramParameter.LINK_STATUS)){
            throw new Exception("Unable to initialize the shader program: " + await _webGLContext.GetProgramInfoLogAsync(shaderProgram));    
        }

        return shaderProgram;
    }

    private async Task<WebGLShader> LoadShader(ShaderType type, string source){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        var shader = await _webGLContext!.CreateShaderAsync(type);   //Create a shader
        await _webGLContext.ShaderSourceAsync(shader, source);      //Load the source code to the shader
        await _webGLContext.CompileShaderAsync(shader);             //Compile the shader

        //Check that the shader compiled successfully
        if(!await _webGLContext.GetShaderParameterAsync<bool>(shader, ShaderParameter.COMPILE_STATUS)){
            throw new Exception("An error occurred when compiling the shaders: " + await _webGLContext.GetShaderInfoLogAsync(shader));    
        }

        return shader;
    }

    private async Task<WebGLBuffer> CreateVertexBuffer(float[] vertices){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        var vertexBuffer = await _webGLContext!.CreateBufferAsync();                         //Create a buffer for the vectors
        await _webGLContext.BindBufferAsync(BufferType.ARRAY_BUFFER, vertexBuffer);         //Bind an empty array to the buffer
        await _webGLContext.BufferDataAsync(BufferType.ARRAY_BUFFER,                        //Send the vectors to WebGL
                                            vertices, 
                                            BufferUsageHint.STATIC_DRAW);
        await _webGLContext.BindBufferAsync(BufferType.ARRAY_BUFFER, null);                 //Unbind the array from the buffer

        return vertexBuffer;
    }

    private async Task<WebGLBuffer> CreateIndexBuffer(ushort[] indexes){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        var indexBuffer = await _webGLContext!.CreateBufferAsync();                          //Create a buffer for the elements
        await _webGLContext.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, indexBuffer);  //Bind an empty array to the buffer
        await _webGLContext.BufferDataAsync(BufferType.ELEMENT_ARRAY_BUFFER,                //Send the elements to WebGL
                                            indexes, 
                                            BufferUsageHint.STATIC_DRAW);
        await _webGLContext.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, null);         //Unbind the array from the buffer

        return indexBuffer;
    }

    private async Task AssociateShadersToBuffers(WebGLBuffer vertexBuffer, WebGLBuffer indexBuffer, WebGLProgram shaderProgram){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        //Bind the buffer objects
        await _webGLContext!.BindBufferAsync(BufferType.ARRAY_BUFFER, vertexBuffer);
        await _webGLContext.BindBufferAsync(BufferType.ELEMENT_ARRAY_BUFFER, indexBuffer);

        //Get the attribute location
        var attribLoc = (uint)await _webGLContext.GetAttribLocationAsync(shaderProgram, _vertexAttribName);

        //Point an attribute to the currently bound vertex buffer object
        await _webGLContext.VertexAttribPointerAsync(attribLoc, 3, DataType.FLOAT, false, 0, 0);

        //Enable the attribute
        await _webGLContext.EnableVertexAttribArrayAsync(attribLoc);
    }

    public async Task Render(){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        await _webGLContext!.UniformMatrixAsync(_pMatrixLoc, false, _pMatrix);
        await _webGLContext.UniformMatrixAsync(_vMatrixLoc, false, _vMatrix);
        await _webGLContext.UniformMatrixAsync(_mMatrixLoc, false, _mMatrix);

        //Clear and draw on the canvas
        await _webGLContext.BeginBatchAsync();
        await _webGLContext.ClearAsync(BufferBits.COLOR_BUFFER_BIT | BufferBits.DEPTH_BUFFER_BIT);
        await _webGLContext.DrawElementsAsync(Primitive.TRIANGLES, _numIndexes, DataType.UNSIGNED_SHORT, 0);
        await _webGLContext.EndBatchAsync();
    }

    private async Task GetMatrixLocs(WebGLProgram shaderProgram){
        if(_webGLContext==null)   {throw new Exception("Must initialise WebGLGraphics first");}

        _pMatrixLoc = await _webGLContext!.GetUniformLocationAsync(shaderProgram, "pMatrix");
        _vMatrixLoc = await _webGLContext.GetUniformLocationAsync(shaderProgram, "vMatrix");
        _mMatrixLoc = await _webGLContext.GetUniformLocationAsync(shaderProgram, "mMatrix");
    }

    public void UpdateProjectionMatrix(float angle, float a, float zMin, float zMax){
        //Prevent divide by 0
        if(zMax <= zMin){
            throw new Exception("Error creating projection matrix: zMax must be greater than zMin.");
        }

        //Calculate the matrix
        float ang = MathF.Tan(0.5f*angle*MathF.PI/180);
        float[] matrix = {  0.5f/ang, 0, 0, 0,
                            0, 0.5f*a/ang, 0, 0,
                            0, 0, -(zMax+zMin)/(zMax-zMin), -1,
                            0, 0, -2*zMax*zMin/(zMax-zMin), 0   };
        
        _pMatrix = matrix;
    }

    public void RotateX(float angle){
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv1 = _mMatrix[1];
        float mv5 = _mMatrix[5];
        float mv9 = _mMatrix[9];

        _mMatrix[1] = c*_mMatrix[1]-s*_mMatrix[2];
        _mMatrix[5] = c*_mMatrix[5]-s*_mMatrix[6];
        _mMatrix[9] = c*_mMatrix[9]-s*_mMatrix[10];
        _mMatrix[2] = c*_mMatrix[2]+s*mv1;
        _mMatrix[6] = c*_mMatrix[6]+s*mv5;
        _mMatrix[10] = c*_mMatrix[10]+s*mv9;
    }

    public void RotateY(float angle){
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv0 = _mMatrix[0];
        float mv4 = _mMatrix[4];
        float mv8 = _mMatrix[8];

        _mMatrix[0] = c*_mMatrix[0]+s*_mMatrix[2];
        _mMatrix[4] = c*_mMatrix[4]+s*_mMatrix[6];
        _mMatrix[8] = c*_mMatrix[8]+s*_mMatrix[10];
        _mMatrix[2] = c*_mMatrix[2]-s*mv0;
        _mMatrix[6] = c*_mMatrix[6]-s*mv4;
        _mMatrix[10] = c*_mMatrix[10]-s*mv8;
    }

    public void RotateZ(float angle){
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        float mv0 = _mMatrix[0];
        float mv4 = _mMatrix[4];
        float mv8 = _mMatrix[8];

        _mMatrix[0] = c*_mMatrix[0]-s*_mMatrix[1];
        _mMatrix[4] = c*_mMatrix[4]-s*_mMatrix[5];
        _mMatrix[8] = c*_mMatrix[8]-s*_mMatrix[9];
        _mMatrix[1] = c*_mMatrix[1]+s*mv0;
        _mMatrix[5] = c*_mMatrix[5]+s*mv4;
        _mMatrix[9] = c*_mMatrix[9]+s*mv8;
    }
}