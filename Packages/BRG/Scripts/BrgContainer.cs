using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace BRG
{
	//TODO transform to floatX
	public class BrgContainer : MonoBehaviour
	{
		public Mesh Mesh;
		public Material Material;
		public ComputeShader Memcpy;
		public BrgItem[] Items;
		
		[Space]
		
		public bool Show = true;

		private BatchRendererGroup _brg;

		private GraphicsBuffer _instanceData;
		private GraphicsBuffer _copySrc;
		private BatchID _batchID;
		private BatchMeshID _meshId;
		private BatchMaterialID _materialId;
		
		private uint _numInstances;

		private const int MatrixSize = sizeof(float) * 4 * 4;
		private const int PackedMatrixSize = sizeof(float) * 4 * 3;
		private const int Float4Size = sizeof(float) * 4;
		private const int BytesPerInstance = (PackedMatrixSize * 2) + Float4Size;
		private const int ExtraBytes = MatrixSize * 2;

		public bool UseConstantBuffer => BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

		public int BufferSize(int bufferCount) => bufferCount * sizeof(int);
		public int BufferOffset => 0;
		public int BufferWindowSize => UseConstantBuffer ? BatchRendererGroup.GetConstantBufferMaxWindowSize() : 0;

		struct PackedMatrix
		{
			public float c0x;
			public float c0y;
			public float c0z;
			public float c1x;
			public float c1y;
			public float c1z;
			public float c2x;
			public float c2y;
			public float c2z;
			public float c3x;
			public float c3y;
			public float c3z;

			public PackedMatrix(Matrix4x4 m)
			{
				c0x = m.m00;
				c0y = m.m10;
				c0z = m.m20;
				c1x = m.m01;
				c1y = m.m11;
				c1z = m.m21;
				c2x = m.m02;
				c2y = m.m12;
				c2z = m.m22;
				c3x = m.m03;
				c3y = m.m13;
				c3z = m.m23;
			}
		}

		private void OnEnable()
		{
			if(Items != null && Items.Length > 0 && Mesh != null && Material != null && Memcpy != null)
				Init();
		}

		private void OnDisable()
		{
			Clear();
		}

		private int BufferCountForInstances(int bytesPerInstance, int numInstances, int extraBytes = 0)
		{
			bytesPerInstance = (bytesPerInstance + sizeof(int) - 1) / sizeof(int) * sizeof(int);
			extraBytes = (extraBytes + sizeof(int) - 1) / sizeof(int) * sizeof(int);
			int totalBytes = bytesPerInstance * numInstances + extraBytes;
			return totalBytes / sizeof(int);
		}

		public void Init(Mesh mesh, Material material, ComputeShader memcpy, IReadOnlyList<BrgItem> items)
		{
			Mesh = mesh;
			Material = material;
			Memcpy = memcpy;
			Items = items.ToArray();

			Init();
		}
		
		public void Init()
		{
			Clear();
			
#if UNITY_EDITOR
		if(!Application.isPlaying)
				return;
#endif
			
			_numInstances = (uint)Items.Length;
			if (_numInstances > 65000)
			{
				Debug.LogError("Max items count is 65k.");
				_numInstances = 65000;
			}
			
			_brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
			_meshId = _brg.RegisterMesh(Mesh);
			_materialId = _brg.RegisterMaterial(Material);

			var target = GraphicsBuffer.Target.Raw;
			if (SystemInfo.graphicsDeviceType is GraphicsDeviceType.OpenGLCore or GraphicsDeviceType.OpenGLES3)
				target |= GraphicsBuffer.Target.Constant;

			int bufferCount = BufferCountForInstances(BytesPerInstance, (int)_numInstances, ExtraBytes);
			_copySrc = new GraphicsBuffer(target,
				bufferCount,
				sizeof(int));
			_instanceData = new GraphicsBuffer(target,
				BufferSize(bufferCount) / sizeof(int),
				sizeof(int));

			var zero = new Matrix4x4[1] { Matrix4x4.zero };

			var matrices = new Matrix4x4[_numInstances];
			var objectToWorld = new PackedMatrix[_numInstances];
			var worldToObject = new PackedMatrix[_numInstances];
			var colors = new Vector4[_numInstances];
			
			for (int i = 0; i < _numInstances; i++)
			{
				var item = Items[i];
				matrices[i] = Matrix4x4.TRS(item.Position, Quaternion.Euler(item.Rotation), item.Scale);
				objectToWorld[i] = new PackedMatrix(matrices[i]);
				worldToObject[i] = new PackedMatrix(matrices[i].inverse);
				colors[i] = (item.Color * Material.color);
			}

			uint byteAddressObjectToWorld = PackedMatrixSize * 2;
			uint byteAddressWorldToObject = (uint)(byteAddressObjectToWorld + PackedMatrixSize * _numInstances);
			uint byteAddressColor = (uint)(byteAddressWorldToObject + PackedMatrixSize * _numInstances);

			_copySrc.SetData(zero, 0, 0, 1);
			_copySrc.SetData(objectToWorld, 0, (int)((byteAddressObjectToWorld + 0) / PackedMatrixSize), objectToWorld.Length);
			_copySrc.SetData(worldToObject, 0, (int)((byteAddressWorldToObject + 0)  / PackedMatrixSize), worldToObject.Length);
			_copySrc.SetData(colors, 0, (int)((byteAddressColor + 0)  / Float4Size), colors.Length);

			int dstSize = _copySrc.count * _copySrc.stride;
			Memcpy.SetBuffer(0, "src", _copySrc);
			Memcpy.SetBuffer(0, "dst", _instanceData);
			Memcpy.SetInt("dstOffset", BufferOffset);
			Memcpy.SetInt("dstSize", dstSize);
			Memcpy.Dispatch(0, dstSize / (64 * 4) + 1, 1, 1);

			var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
			metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | byteAddressObjectToWorld, };
			metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | byteAddressWorldToObject, };
			metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"), Value = 0x80000000 | byteAddressColor, };

			_batchID = _brg.AddBatch(metadata, _instanceData.bufferHandle, (uint)BufferOffset, (uint)BufferWindowSize);
		}

		private void Clear()
		{
			if (_brg != null)
			{
				_copySrc.Dispose();
				_copySrc = null;
				_instanceData.Dispose();
				_instanceData = null;
				_brg.Dispose();
				_brg = null;
			}
		}

		private unsafe JobHandle OnPerformCulling(
			BatchRendererGroup rendererGroup,
			BatchCullingContext cullingContext,
			BatchCullingOutput cullingOutput,
			IntPtr userContext)
		{
			if (!Show)
			{
				return new JobHandle();
			}
			
			int alignment = UnsafeUtility.AlignOf<long>();

			var drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

			drawCommands->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>(), alignment, Allocator.TempJob);
			drawCommands->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), alignment, Allocator.TempJob);
			drawCommands->visibleInstances = (int*)UnsafeUtility.Malloc(_numInstances * sizeof(int), alignment, Allocator.TempJob);
			drawCommands->drawCommandPickingInstanceIDs = null;

			drawCommands->drawCommandCount = 1;
			drawCommands->drawRangeCount = 1;
			drawCommands->visibleInstanceCount = (int)_numInstances;

			drawCommands->instanceSortingPositions = null;
			drawCommands->instanceSortingPositionFloatCount = 0;

			drawCommands->drawCommands[0].visibleOffset = 0;
			drawCommands->drawCommands[0].visibleCount = _numInstances;
			drawCommands->drawCommands[0].batchID = _batchID;
			drawCommands->drawCommands[0].materialID = _materialId;
			drawCommands->drawCommands[0].meshID = _meshId;
			drawCommands->drawCommands[0].submeshIndex = 0;
			drawCommands->drawCommands[0].splitVisibilityMask = 0xff;
			drawCommands->drawCommands[0].flags = 0;
			drawCommands->drawCommands[0].sortingPosition = 0;

			drawCommands->drawRanges[0].drawCommandsBegin = 0;
			drawCommands->drawRanges[0].drawCommandsCount = 1;
			drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings { renderingLayerMask = 0xffffffff, };

			for (int i = 0; i < _numInstances; ++i)
				drawCommands->visibleInstances[i] = i;

			return new JobHandle();
		}
	}
}