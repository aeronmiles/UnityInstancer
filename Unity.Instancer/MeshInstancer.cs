using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;
using System;

[Serializable]
public class MeshInstancerSettings
{
    [Header("References")]
    public PointCacheData SpawnPointCache;
    public GameObject MeshContainer;
    public Mesh InstanceMesh = null;
    public Material Material => MeshContainer.GetComponent<MeshRenderer>().sharedMaterial;

    [Header("Instance Settings")]
    public int Count = 50000;
    public int MaxInstancesPerMesh = 10000;
    public int Rate = 10000;
    public uint Seed = 55555;
    public float Scale = 0.08f;
    public float MinSize = 0.0022714f;
    public float MaxSize = 0.0037857f;
    [Header("Job Settings")]
    public int InnerLoopBatchCount = 10000;

}


public class MeshInstancer : MonoBehaviour
{
    [Header("Settings")]
    public MeshInstancerSettings Settings;
    public bool Complete => instanceJobs.Complete;
    public int SpawnCount
    {
        set
        {
            Settings.Count = value;
        }
    }

    MeshInstanceJob instanceJobs;

    private void Update()
    {
        if (instanceJobs != null && !instanceJobs.Complete) instanceJobs.Run();
    }

    public void Spawn()
    {
        Dispose();

        var p = transform.position;
        Settings.Material.SetVector("_TransformPosition", new Vector4(p.x, p.y, p.z, 0f));

        instanceJobs = new MeshInstanceJob(Settings, transform);
        instanceJobs.Run();
    }

    void OnDisable()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (instanceJobs != null) instanceJobs.Dispose();
    }
}

public class MeshInstanceWriter
{
    Mesh mesh;
    public Mesh Mesh => mesh;
    GameObject gameObj;
    public GameObject GameObject => gameObj;

    public int InstanceCount = 0;

    public void Destroy() => GameObject.Destroy(gameObj);

    public MeshInstanceWriter(MeshInstancerSettings settings, Transform parent)
    {
        gameObj = GameObject.Instantiate(settings.MeshContainer, parent);
        mesh = new Mesh();
        mesh.SetVertexBufferParams(settings.Rate * settings.InstanceMesh.vertexCount, VertexPN.VertexAttributes);
        mesh.SetIndexBufferParams(settings.Rate * (int)settings.InstanceMesh.GetIndexCount(0), IndexFormat.UInt32);
        gameObj.GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    public void SetBufferData(ref InstanceVertexJob vJob, ref InstaceIndexJob iJob, int count)
    {
        mesh.SetVertexBufferData(vJob.outVertexBuffers, 0, InstanceCount * vJob.vertexCount, count * vJob.vertexCount, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds);
        mesh.SetIndexBufferData(iJob.outIndexBuffers, 0, InstanceCount * iJob.indexCount, count * iJob.indexCount, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds);

        InstanceCount += count;

        SubMeshDescriptor sm = new SubMeshDescriptor(0, InstanceCount * vJob.vertexCount, MeshTopology.Triangles);
        sm.indexCount = InstanceCount * iJob.indexCount;
        sm.vertexCount = InstanceCount * vJob.vertexCount;
        sm.firstVertex = 0;
        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, sm);
    }
}

public class MeshInstanceJob
{
    MeshInstancerSettings settings;
    int vertexCount;
    int indexCount;
    Mesh.MeshDataArray instanceMeshDataArray;
    Mesh.MeshData instanceMeshData;
    MeshInstanceWriter[] meshWriters;
    int currentMesh = 0;

    int spawnRateCount;
    public int SpawnedCount { get; private set; }
    public bool Complete => SpawnedCount >= settings.Count;
    int NextSpawnAmount
    {
        get
        {
            if (spawnRateCount + SpawnedCount < settings.Count) return spawnRateCount;
            else return settings.Count - SpawnedCount;
        }
    }

    InstanceVertexJob vJob;
    InstaceIndexJob iJob;
    NativeArray<JobHandle> jobHandles;


    public MeshInstanceJob(MeshInstancerSettings settings, Transform parent)
    {
        this.settings = settings;
        spawnRateCount = settings.Rate / 30;
        vertexCount = settings.InstanceMesh.vertexCount;
        indexCount = (int)settings.InstanceMesh.GetIndexCount(0);
        instanceMeshDataArray = Mesh.AcquireReadOnlyMeshData(settings.InstanceMesh);
        instanceMeshData = instanceMeshDataArray[0];
        int meshCount = (int)(math.ceil(settings.Count / (float)settings.MaxInstancesPerMesh));
        meshWriters = new MeshInstanceWriter[meshCount];
        for (int i = 0; i < meshCount; i++)
            meshWriters[i] = new MeshInstanceWriter(settings, parent);
    }

    public void DisposeJobs()
    {
        try { vJob.Dispose(); } catch { }
        try { iJob.Dispose(); } catch { }
        try { jobHandles.Dispose(); } catch { }
    }

    // TODO remove
    public void Dispose()
    {
        DisposeJobs();
        foreach (var mr in meshWriters) mr.Destroy();
        try { instanceMeshDataArray.Dispose(); } catch { }
    }

    public void Run()
    {
        int spawnCount = NextSpawnAmount;
        if (spawnCount > settings.MaxInstancesPerMesh - meshWriters[currentMesh].InstanceCount)
        {
            int spawnRemainder = settings.MaxInstancesPerMesh - meshWriters[currentMesh].InstanceCount;
            spawnInstances(spawnRemainder, meshWriters[currentMesh]);

            spawnCount -= spawnRemainder;
            currentMesh++;
        }
        spawnInstances(spawnCount, meshWriters[currentMesh]);
    }

    void spawnInstances(int count, MeshInstanceWriter meshWriter)
    {
        vJob = newVJob(count);
        iJob = newIJob(count, meshWriter.InstanceCount);

        jobHandles = new NativeArray<JobHandle>(2, Allocator.Temp);
        jobHandles[0] = vJob.ScheduleParallel(vJob.outVertexBuffers.Length, settings.InnerLoopBatchCount, new JobHandle());
        jobHandles[1] = iJob.ScheduleParallel(iJob.outIndexBuffers.Length, settings.InnerLoopBatchCount, new JobHandle());
        JobHandle.CompleteAll(jobHandles);
        jobHandles.Dispose();

        SpawnedCount += count;

        meshWriter.SetBufferData(ref vJob, ref iJob, count);

        DisposeJobs();
    }

    InstaceIndexJob newIJob(int count, int indexOffset)
    {
        var iJob = new InstaceIndexJob();
        iJob.vertexCount = vertexCount;
        iJob.indexCount = indexCount;

        iJob.indicies = new NativeArray<int>(iJob.indexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        // TODO : optimize
        instanceMeshData.GetIndices(iJob.indicies, 0);

        iJob.outIndexBuffers = new NativeArray<int>(count * iJob.indexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        iJob.indexOffset = indexOffset * vertexCount;

        return iJob;
    }

    InstanceVertexJob newVJob(int count)
    {
        var vJob = new InstanceVertexJob();
        vJob.seed = settings.Seed + (uint)SpawnedCount;
        vJob.scale = settings.Scale;
        vJob.minSize = settings.MinSize;
        vJob.maxSize = settings.MaxSize;
        vJob.vertexCount = vertexCount;

        vJob.positions = new NativeArray<float3>(count, Allocator.TempJob);
        for (int i = 0; i < count; i++)
            vJob.positions[i] = settings.SpawnPointCache.Points[SpawnedCount + i];

        int nV = count * vertexCount;
        vJob.vertices = new NativeArray<float3>(nV, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        vJob.normals = new NativeArray<float3>(nV, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        // TODO : optimize
        instanceMeshData.GetVertices(vJob.vertices.Reinterpret<Vector3>());
        instanceMeshData.GetNormals(vJob.normals.Reinterpret<Vector3>());

        vJob.outVertexBuffers = new NativeArray<VertexPN>(nV, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        return vJob;
    }
}


[BurstCompile]
public struct InstanceVertexJob : IJobFor
{
    [ReadOnly] public uint seed;
    [ReadOnly] public float minSize;
    [ReadOnly] public float maxSize;
    [ReadOnly] public float scale;
    [ReadOnly] public int vertexCount;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> positions;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> vertices;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<float3> normals;

    [WriteOnly] public NativeArray<VertexPN> outVertexBuffers;

    public void Dispose()
    {
        outVertexBuffers.Dispose();
    }

    public void Execute(int index)
    {
        int instanceN = (int)math.floor(index / (float)vertexCount);
        Random rand = new Random(seed + (uint)instanceN);
        float4x4 mat = float4x4.TRS(
            positions[instanceN] * scale,
            quaternion.Euler(rand.NextFloat3(360f)),
            new float3(math.lerp(minSize, maxSize, rand.NextFloat()))
        );

        VertexPN v = new VertexPN();
        v.pos = math.mul(mat, new float4(vertices[index % vertexCount], 1)).xyz;
        v.normal = math.normalize(math.mul(mat, new float4(normals[index % vertexCount], 0)).xyz);
        outVertexBuffers[index] = v;
    }
}

[BurstCompile]
public struct InstaceIndexJob : IJobFor
{
    [ReadOnly] public int vertexCount;
    [ReadOnly] public int indexCount;
    [ReadOnly] public int indexOffset;
    [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<int> indicies;

    [WriteOnly] public NativeArray<int> outIndexBuffers;

    public void Dispose()
    {
        outIndexBuffers.Dispose();
    }

    public void Execute(int index)
    {
        int instanceN = (int)math.floor(index / (float)indexCount);
        outIndexBuffers[index] = indicies[index % indexCount] + (instanceN * vertexCount) + indexOffset;
    }
}