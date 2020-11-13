// REFERENCE
// Not yet implemented / tested / optimized
// Using Jobified Mesh.AllocateWritableMeshData
// https://docs.google.com/document/d/1QC7NV7JQcvibeelORJvsaTReTyszllOlxdfEsaVL2oA/edit#heading=h.vyksohcynwk5

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

[BurstCompile]
struct MeshInstanceWriterJob : IJobParallelFor
{
    [ReadOnly] public uint seed;
    [ReadOnly] public float scale;
    [ReadOnly] public int vertexCount;
    [ReadOnly] public int indexCount;
    [ReadOnly] public Mesh.MeshData outMeshData;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> positions;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> vertices;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> normals;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<int> indicies;

    [WriteOnly] public NativeArray<Vector3> bufferPOS;
    [WriteOnly] public NativeArray<Vector3> bufferNORM;
    [WriteOnly] public NativeArray<int> bufferIND;

    public NativeArray<float3x2> bounds;

    public void Dispose()
    {
        bufferPOS.Dispose();
        bufferNORM.Dispose();
        bufferIND.Dispose();
    }

    public void Execute(int index)
    {
        Random rand = new Random(seed + (uint)index);
        float4x4 mat = float4x4.TRS(
            positions[index],
            quaternion.Euler(rand.NextFloat3(360f)),
            new float3(math.lerp(0.2f, 1f, rand.NextFloat())) * scale
        );

        var bufferPOS = outMeshData.GetVertexData<float3>();
        var bufferNORM = outMeshData.GetVertexData<float3>(stream: 1);

        // Transform input mesh vertices/normals
        // compute transformed mesh bounds.
        var b = bounds[index];
        int nV = vertexCount;
        int vStart = nV * index;
        for (int i = 0; i < nV; ++i)
        {
            var pos = vertices[i];
            bufferPOS[i + vStart] = math.mul(mat, new float4(pos, 1)).xyz;
            var norm = normals[i];
            bufferNORM[i + vStart] = math.normalize(math.mul(mat, new float4(norm, 0)).xyz);

            b.c0 = math.min(b.c0, pos);
            b.c1 = math.max(b.c1, pos);
        }
        bounds[index] = b;

        var bufferIND = outMeshData.GetIndexData<int>();
        int nI = indexCount;
        int iStart = nI * index;
        for (int i = 0; i < nI; i++)
        {
            bufferIND[i + iStart] = indicies[i] + (int)vStart;
        }
    }
}
