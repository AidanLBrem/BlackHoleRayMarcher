using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static UnityEngine.Mathf;
//Gotta be honest here, this is almost 1:1 Sebastian Lague's code. I still need to learn how to do this myself well
public static class ShaderHelper
{
	public enum DepthMode { None = 0, Depth16 = 16, Depth24 = 24 }
    public static readonly GraphicsFormat RGBA_SFloat = GraphicsFormat.R32G32B32A32_SFloat;
	
    public static void InitMaterial(Shader shader, ref Material material) {
        if (material == null || (material.shader != shader && shader != null)) {
            if (shader == null) {
                shader = Shader.Find("Unlit/Texture");
            }
            material = new Material(shader);
        }
    }
public static RenderTexture CreateRenderTexture(int width, int height, FilterMode filterMode, GraphicsFormat format, string name = "Unnamed", DepthMode depthMode = DepthMode.None, bool useMipMaps = false)
	{
		RenderTexture texture = new RenderTexture(width, height, (int)depthMode);
		texture.graphicsFormat = format;
		texture.enableRandomWrite = true;
		texture.autoGenerateMips = false;
		texture.useMipMap = useMipMaps;
		texture.Create();

		texture.name = name;
		texture.wrapMode = TextureWrapMode.Clamp;
		texture.filterMode = filterMode;
		return texture;
	}

	public static void CreateRenderTexture(ref RenderTexture texture, RenderTexture template)
	{
		if (texture != null)
		{
			texture.Release();
		}
		texture = new RenderTexture(template.descriptor);
		texture.enableRandomWrite = true;
		texture.Create();
	}

	public static bool CreateRenderTexture(ref RenderTexture texture, int width, int height, FilterMode filterMode, GraphicsFormat format, string name = "Unnamed", DepthMode depthMode = DepthMode.None, bool useMipMaps = false)
	{
		if (texture == null || !texture.IsCreated() || texture.width != width || texture.height != height || texture.graphicsFormat != format || texture.depth != (int)depthMode || texture.useMipMap != useMipMaps)
		{
			if (texture != null)
			{
				texture.Release();
			}
			texture = CreateRenderTexture(width, height, filterMode, format, name, depthMode, useMipMaps);
			return true;
		}
		else
		{
			texture.name = name;
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.filterMode = filterMode;
		}

		return false;
	}
    public static void CreateStructuredBuffer<T>(ref ComputeBuffer buffer, int count)
	{
		count = Mathf.Max(1, count); // cannot create 0 length buffer
		int stride = GetStride<T>();
		bool createNewBuffer = buffer == null || !buffer.IsValid() || buffer.count != count || buffer.stride != stride;
		if (createNewBuffer)
		{
			Release(buffer);
			buffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
		}
	}

	public static ComputeBuffer CreateStructuredBuffer<T>(T[] data)
	{
		var buffer = new ComputeBuffer(data.Length, GetStride<T>());
		buffer.SetData(data);
		return buffer;
	}

	public static ComputeBuffer CreateStructuredBuffer<T>(List<T> data) where T : struct
	{
		var buffer = new ComputeBuffer(data.Count, GetStride<T>());
		buffer.SetData<T>(data);
		return buffer;
	}
	public static int GetStride<T>() => System.Runtime.InteropServices.Marshal.SizeOf(typeof(T));

	public static ComputeBuffer CreateStructuredBuffer<T>(int count)
	{
		return new ComputeBuffer(count, GetStride<T>());
	}


	// Create a compute buffer containing the given data (Note: data must be blittable)
	public static void CreateStructuredBuffer<T>(ref ComputeBuffer buffer, T[] data) where T : struct
	{
		// Cannot create 0 length buffer (not sure why?)
		int length = Max(1, data.Length);
		// The size (in bytes) of the given data type
		int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(T));

		// If buffer is null, wrong size, etc., then we'll need to create a new one
		if (buffer == null || !buffer.IsValid() || buffer.count != length || buffer.stride != stride)
		{
			if (buffer != null) { buffer.Release(); }
			buffer = new ComputeBuffer(length, stride, ComputeBufferType.Structured);
		}

		buffer.SetData(data);
	}

	// Create a compute buffer containing the given data (Note: data must be blittable)
	public static void CreateStructuredBuffer<T>(ref ComputeBuffer buffer, List<T> data) where T : struct
	{
		// Cannot create 0 length buffer (not sure why?)
		int length = Max(1, data.Count);
		// The size (in bytes) of the given data type
		int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(T));

		// If buffer is null, wrong size, etc., then we'll need to create a new one
		if (buffer == null || !buffer.IsValid() || buffer.count != length || buffer.stride != stride)
		{
			if (buffer != null) { buffer.Release(); }
			buffer = new ComputeBuffer(length, stride, ComputeBufferType.Structured);
		}

		buffer.SetData(data);
	}
    public static void Release(ComputeBuffer buffer)
	{
		if (buffer != null)
		{
			buffer.Release();
		}
	}

}
