using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public class StripProBuilderShaders : IPreprocessShaders
{
    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, 
        ShaderSnippetData snippet, 
        IList<ShaderCompilerData> data)
    {
        if (shader.name.Contains("ProBuilder"))
            data.Clear();
    }
}