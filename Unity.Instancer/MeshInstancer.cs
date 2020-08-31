using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;
using System;

[ExecuteInEditMode]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MeshInstancer : MonoBehaviour
{

    [Header("Settings")]
    [SerializeField] uint _randomSeed = 100;
    [SerializeField] bool randomizeSeed = true;
    [SerializeField] Mesh mesh = null;
    [SerializeField] int instanceCount = 20;
    [SerializeField] float scale = 1f;

    #region private members
    MeshFilter _meshFilter;
    MeshRenderer _meshRenderer;

    Mesh _mesh;
    float3[] protoVerts = null;
    float3[] protoNormals = null;
    uint[] protoIndexes = null;



    int indexCount => mesh.triangles.Length;
    int vertexCount => mesh.vertexCount;
    vertexBufferJob jobVerts;
    IndexBufferJob jobIndexes;
    JobHandle jobVertsHandle;
    JobHandle jobIndexesHandle;
    #endregion


    void OnEnable()
    {
        _mesh = new Mesh();

        _meshFilter = gameObject.GetComponent<MeshFilter>();
        _meshFilter.sharedMesh = _mesh;

        _meshRenderer = gameObject.GetComponent<MeshRenderer>();

        setMesh();
    }

    private void setMesh()
    {
        int verCount = vertexCount;
        protoVerts = new float3[verCount];
        protoNormals = new float3[verCount];
        var verts = mesh.vertices;
        var norms = mesh.normals;
        for (int i = 0; i < verCount; i++)
        {
            protoVerts[i] = verts[i];
            protoNormals[i] = norms[i];
        }

        int indCount = mesh.triangles.Length;
        protoIndexes = new uint[indCount];
        int[] inds = mesh.triangles;
        for (int i = 0; i < indCount; i++)
        {
            protoIndexes[i] = (uint)inds[i];
        }
    }

    private bool hasCompleted = true;
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space) || Input.touchCount > 0 && Input.touches[0].phase == TouchPhase.Began)
        {
            UpdateMesh();
            hasCompleted = false;
        }
        if (!hasCompleted && jobVertsHandle.IsCompleted && jobIndexesHandle.IsCompleted)
        {
            jobVertsHandle.Complete();
            jobIndexesHandle.Complete();
            InitializeMesh(jobVerts, jobIndexes);

            jobVerts.Dispose();
            jobIndexes.Dispose();
            hasCompleted = true;
        }
    }
    void OnDestroy()
    {
#if UNITY_EDITOR
        if (_mesh != null) DestroyImmediate(_mesh);
#else
        if (_mesh != null) Destroy(_mesh);
#endif
    }

    void InitializeMesh(vertexBufferJob vertJob, IndexBufferJob indexJob)
    {
        _mesh.SetVertexBufferParams(
            vertJob.bufferPositions.Length,
            new VertexAttributeDescriptor
                (VertexAttribute.Position, VertexAttributeFormat.Float32, 3)
        // TODO add interleaved pos & normal buffers
        //     normalBuffer.Length,
        //     new VertexAttributeDescriptor
        //         (VertexAttribute.Normal, VertexAttributeFormat.Float32, 3)
        );
        _mesh.SetVertexBufferData(vertJob.bufferPositions, 0, 0, vertJob.bufferPositions.Length);
        // TODO add interleaved pos & normal buffers
        // _mesh.SetVertexBufferData(interleavedPosNormBuffer, 0, 0, postionBuffer.Length);


        _mesh.SetIndexBufferParams(indexJob.bufferIndexes.Length, IndexFormat.UInt32);
        _mesh.SetIndexBufferData(indexJob.bufferIndexes, 0, 0, indexJob.bufferIndexes.Length);

        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexJob.bufferIndexes.Length));

        _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
        // TODO remove
        _mesh.RecalculateNormals();
    }

    void UpdateMesh()
    {
        _randomSeed = randomizeSeed ? (uint)UnityEngine.Random.Range(0, int.MaxValue) : _randomSeed;
        jobVerts = new vertexBufferJob
        {
            vertexCount = vertexCount,
            seed = _randomSeed,
            scale = scale,
            bufferPositions = new NativeArray<float3>(
            vertexCount * instanceCount, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
            ),
            bufferNormals = new NativeArray<float3>(
            vertexCount * instanceCount, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
            ),
            positions = new NativeArray<float3>(protoVerts, Allocator.Persistent),
            normals = new NativeArray<float3>(protoNormals, Allocator.Persistent),
        };

        jobIndexes = new IndexBufferJob
        {
            vertexCount = vertexCount,
            indexCount = indexCount,
            bufferIndexes = new NativeArray<uint>(
            indexCount * instanceCount, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
            ),
            indexes = new NativeArray<uint>(protoIndexes, Allocator.Persistent)
        };

        jobVertsHandle = jobVerts.Schedule(instanceCount * vertexCount, vertexCount);
        jobIndexesHandle = jobIndexes.Schedule(instanceCount * indexCount, indexCount);
    }

    [BurstCompile(CompileSynchronously = true)]
    struct IndexBufferJob : IJobParallelFor
    {
        [ReadOnly] public int vertexCount;
        [ReadOnly] public int indexCount;
        [ReadOnly] public NativeArray<uint> indexes;
        [WriteOnly] public NativeArray<uint> bufferIndexes;

        public void Execute(int index)
        {
            float instanceN = math.floor(index / (float)indexCount);
            uint vertOffset = (uint)(instanceN * vertexCount);
            bufferIndexes[index] = vertOffset + indexes[index % indexCount];
        }

        internal void Dispose()
        {
            indexes.Dispose();
            bufferIndexes.Dispose();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct vertexBufferJob : IJobParallelFor
    {
        [ReadOnly] public float vertexCount;
        [ReadOnly] public uint seed;
        [ReadOnly] public float scale;

        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> normals;

        [WriteOnly] public NativeArray<float3> bufferPositions;
        [WriteOnly] public NativeArray<float3> bufferNormals;

        public void Execute(int index)
        {
            int vertIndex = index % (int)vertexCount;
            uint instanceN = (uint)math.floor(index / vertexCount);
            var rand = new Random(seed + (uint)instanceN);

            float4x4 iXform = float4x4.TRS(rand.NextFloat3(),
            quaternion.Euler(rand.NextFloat3(360f)),
            new float3(math.lerp(0.2f, 1f, rand.NextFloat())) * scale);

            float4 m = math.mul(iXform, new float4(positions[vertIndex], 1f));
            float3 pos = new float3(m[0], m[1], m[2]);
            bufferPositions[index] = pos; //positions[vertIndex]; 
            // bufferNormals[index] = normals[vertIndex];
        }

        public void Dispose()
        {
            positions.Dispose();
            normals.Dispose();
            bufferPositions.Dispose();
            bufferNormals.Dispose();
        }
    }

}
