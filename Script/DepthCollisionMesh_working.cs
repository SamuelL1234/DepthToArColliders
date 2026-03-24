using UnityEngine;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

/// <summary>
/// Receives depth + color from Python.
/// Renders a textured mesh for visuals.
/// Builds voxel-based collision with greedy plane merging.
/// </summary>
public class DepthCollisionMesh : MonoBehaviour
{
    [Header("Connection")]
    public string host = "127.0.0.1";
    public int port = 9999;
    public bool autoConnect = true;

    [Header("Mesh Settings")]
    public float meshScale = 1.0f;
    public float maxDepth = 6.0f;
    public int downsample = 1;
    public bool invertDepth = false;

    [Header("Voxel Collision")]
    [Tooltip("Size of each voxel in meters")]
    public float voxelSize = 0.08f;

    [Tooltip("Min points in a voxel to count as occupied")]
    public int minPointsPerVoxel = 1;

    [Tooltip("Min occupied voxels in a Y-layer to count as a plane (extends floor)")]
    public int minVoxelsForPlane = 12;

    [Tooltip("Auto-level floor by finding lowest large XZ surface")]
    public bool autoLevel = true;

    [Tooltip("How fast the leveling correction applies")]
    [Range(0.01f, 0.3f)]
    public float levelSmoothing = 0.08f;

    [Tooltip("Manual override: camera tilt X in degrees")]
    [Range(-45f, 45f)]
    public float cameraTiltX = 0f;

    [Tooltip("Manual override: camera tilt Z in degrees")]
    [Range(-45f, 45f)]
    public float cameraTiltZ = 0f;

    [Tooltip("Show voxel colliders as wireframe cubes")]
    public bool showVoxels = false;

    [Tooltip("Color for plane colliders")]
    public Color planeColor = new Color(0.2f, 0.8f, 0.2f, 0.3f);

    [Tooltip("Color for object colliders")]
    public Color objectColor = new Color(0.8f, 0.2f, 0.2f, 0.3f);

    [Header("Visualization")]
    public bool showMesh = true;

    // Connection
    private TcpClient client;
    private NetworkStream stream;
    private Thread receiveThread;
    private volatile bool connected = false;
    private volatile bool running = true;

    // Buffer
    private float[] depthBuffer;
    private byte[] colorJpegBuffer;
    private int depthWidth, depthHeight;
    private float fx, fy, cx, cy;
    private volatile bool newDataAvailable = false;
    private readonly object bufferLock = new object();

    // Visual mesh
    private Mesh visualMesh;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Texture2D colorTexture;
    private Material textureMaterial;

    // Voxel colliders
    private GameObject colliderParent;
    private List<GameObject> activeColliders = new List<GameObject>();
    private Material planeMat;
    private Material objectMat;

    // Floor leveling
    private Quaternion floorCorrection = Quaternion.identity;
    private float currentYOffset = 0;

    public float FloorY { get; private set; }
    public bool IsConnected => connected;

    void Start()
    {
        visualMesh = new Mesh();
        visualMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        if (showMesh)
        {
            meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
            textureMaterial = new Material(Shader.Find("Unlit/Texture"));
            meshRenderer.material = textureMaterial;
        }

        colliderParent = new GameObject("VoxelColliders");
        colliderParent.transform.SetParent(transform);

        if (showVoxels)
        {
            planeMat = MakeTransparentMat(planeColor);
            objectMat = MakeTransparentMat(objectColor);
        }

        if (autoConnect)
            Connect();
    }

    Material MakeTransparentMat(Color c)
    {
        var mat = new Material(Shader.Find("Standard"));
        mat.color = c;
        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.renderQueue = 3000;
        return mat;
    }

    public void Connect()
    {
        receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        receiveThread.Start();
    }

    void ReceiveLoop()
    {
        while (running)
        {
            try
            {
                if (!connected)
                {
                    Debug.Log($"[DepthMesh] Connecting to {host}:{port}...");
                    client = new TcpClient();
                    client.Connect(host, port);
                    stream = client.GetStream();
                    connected = true;
                    Debug.Log("[DepthMesh] Connected!");
                }

                byte[] header = ReadExact(24);
                if (header == null) { Disconnect(); continue; }

                int w = BitConverter.ToInt32(header, 0);
                int h = BitConverter.ToInt32(header, 4);
                float _fx = BitConverter.ToSingle(header, 8);
                float _fy = BitConverter.ToSingle(header, 12);
                float _cx = BitConverter.ToSingle(header, 16);
                float _cy = BitConverter.ToSingle(header, 20);

                int depthSize = w * h * 4;
                byte[] depthBytes = ReadExact(depthSize);
                if (depthBytes == null) { Disconnect(); continue; }

                float[] depths = new float[w * h];
                Buffer.BlockCopy(depthBytes, 0, depths, 0, depthSize);

                byte[] jpegLenBytes = ReadExact(4);
                if (jpegLenBytes == null) { Disconnect(); continue; }
                int jpegLen = BitConverter.ToInt32(jpegLenBytes, 0);

                byte[] jpegData = ReadExact(jpegLen);
                if (jpegData == null) { Disconnect(); continue; }

                lock (bufferLock)
                {
                    depthWidth = w; depthHeight = h;
                    fx = _fx; fy = _fy; cx = _cx; cy = _cy;
                    depthBuffer = depths;
                    colorJpegBuffer = jpegData;
                    newDataAvailable = true;
                }
            }
            catch (Exception e)
            {
                if (running) { Debug.LogWarning($"[DepthMesh] {e.Message}"); Disconnect(); Thread.Sleep(1000); }
            }
        }
    }

    byte[] ReadExact(int count)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int r = stream.Read(buf, off, count - off);
            if (r == 0) return null;
            off += r;
        }
        return buf;
    }

    void Disconnect()
    {
        connected = false;
        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }
    }

    void Update()
    {
        if (!newDataAvailable) return;

        float[] depths; byte[] jpegData;
        int w, h; float _fx, _fy, _cx, _cy;

        lock (bufferLock)
        {
            depths = depthBuffer; jpegData = colorJpegBuffer;
            w = depthWidth; h = depthHeight;
            _fx = fx; _fy = fy; _cx = cx; _cy = cy;
            newDataAvailable = false;
        }

        if (colorTexture == null || colorTexture.width != w || colorTexture.height != h)
        {
            colorTexture = new Texture2D(w, h, TextureFormat.RGB24, false);
            colorTexture.filterMode = FilterMode.Bilinear;
            colorTexture.wrapMode = TextureWrapMode.Clamp;
        }
        colorTexture.LoadImage(jpegData);
        if (textureMaterial != null)
            textureMaterial.mainTexture = colorTexture;

        // Compute tilt correction
        if (autoLevel)
        {
            var rawPoints = BackProject(depths, w, h, _fx, _fy, _cx, _cy);
            UpdateFloorCorrection(rawPoints);
        }
        else
        {
            floorCorrection = Quaternion.Euler(cameraTiltX, 0, cameraTiltZ);
        }

        // Back-project and correct points for voxels
        var points = BackProject(depths, w, h, _fx, _fy, _cx, _cy);
        var correctedPoints = new List<Vector3>(points.Count);
        foreach (var p in points)
            correctedPoints.Add(floorCorrection * p);

        // Build voxels first — sets currentYOffset
        BuildVoxelColliders(correctedPoints);

        // Rebuild visual mesh now that yOffset is known
        BuildVisualMesh(depths, w, h, _fx, _fy, _cx, _cy);
    }

    /// <summary>
    /// Voxel-column floor detection:
    /// 1. Voxelize into XZ columns
    /// 2. For each column, find the lowest occupied Y
    /// 3. The floor = the set of columns whose lowest Y covers the biggest XZ area
    /// 4. Fit a plane to those (x, lowestY, z) points and rotate to level it
    /// </summary>
    void UpdateFloorCorrection(List<Vector3> points)
    {
        if (points.Count < 50) return;

        float vs = voxelSize;

        // Step 1: For each XZ column, track the lowest Y and accumulate world coords
        // Key: packed(voxelX, voxelZ), Value: (lowestVoxelY, sumWorldX, sumWorldY, sumWorldZ, count)
        var columns = new Dictionary<long, float[]>();

        foreach (var p in points)
        {
            int vx = Mathf.FloorToInt(p.x / vs);
            int vz = Mathf.FloorToInt(p.z / vs);
            int vy = Mathf.FloorToInt(p.y / vs);
            long key = ((long)vx << 32) | (uint)vz;

            if (!columns.ContainsKey(key))
            {
                // [lowestVoxelY, sumX, sumY, sumZ, count]
                columns[key] = new float[] { vy, p.x, p.y, p.z, 1 };
            }
            else
            {
                var col = columns[key];
                if (vy < col[0])
                {
                    // New lowest — reset sums to only track lowest-Y points
                    col[0] = vy;
                    col[1] = p.x; col[2] = p.y; col[3] = p.z; col[4] = 1;
                }
                else if (vy == (int)col[0])
                {
                    // Same lowest layer — accumulate
                    col[1] += p.x; col[2] += p.y; col[3] += p.z; col[4]++;
                }
            }
        }

        if (columns.Count < 10) return;

        // Step 3: Find the global lowest Y across all columns
        // Only columns whose lowest Y is near this global minimum are true floor
        int globalMinLowestY = int.MaxValue;
        foreach (var kv in columns)
        {
            int lowestY = (int)kv.Value[0];
            if (lowestY < globalMinLowestY) globalMinLowestY = lowestY;
        }

        // Collect floor samples: only from columns within a few voxels of the absolute bottom
        // This filters out tables, sofas, shelves — their lowest Y is above the real floor
        int floorTolerance = 3; // ~24cm at 0.08 voxel size
        var floorSamples = new List<Vector3>();

        foreach (var kv in columns)
        {
            var col = kv.Value;
            int lowestY = (int)col[0];

            // Skip columns whose bottom is above the global floor level
            if (lowestY > globalMinLowestY + floorTolerance) continue;

            float count = col[4];
            Vector3 avgPos = new Vector3(col[1] / count, col[2] / count, col[3] / count);
            floorSamples.Add(avgPos);
        }

        if (floorSamples.Count < 5) return;

        // Step 4: Fit plane y = ax + bz + c to the floor samples
        double sX = 0, sZ = 0, sY = 0;
        double sXX = 0, sXZ = 0, sZZ = 0;
        double sXY = 0, sZY = 0;
        int n = floorSamples.Count;

        foreach (var p in floorSamples)
        {
            sX += p.x;  sZ += p.z;  sY += p.y;
            sXX += p.x * p.x;  sXZ += p.x * p.z;  sZZ += p.z * p.z;
            sXY += p.x * p.y;  sZY += p.z * p.y;
        }

        double d00 = sXX, d01 = sXZ, d02 = sX;
        double d10 = sXZ, d11 = sZZ, d12 = sZ;
        double d20 = sX,  d21 = sZ,  d22 = n;

        double det = d00 * (d11 * d22 - d12 * d21)
                   - d01 * (d10 * d22 - d12 * d20)
                   + d02 * (d10 * d21 - d11 * d20);

        if (System.Math.Abs(det) < 1e-10) return;

        double r0 = sXY, r1 = sZY, r2 = sY;

        double a = (r0 * (d11 * d22 - d12 * d21)
                  - d01 * (r1 * d22 - d12 * r2)
                  + d02 * (r1 * d21 - d11 * r2)) / det;

        double b = (d00 * (r1 * d22 - d12 * r2)
                  - r0 * (d10 * d22 - d12 * d20)
                  + d02 * (d10 * r2 - r1 * d20)) / det;

        // Plane normal = (-a, 1, -b) normalized
        Vector3 normal = new Vector3((float)-a, 1f, (float)-b).normalized;
        if (normal.y < 0) normal = -normal;

        // Clamp correction to reasonable range (max 45 degrees)
        float angle = Vector3.Angle(normal, Vector3.up);
        if (angle > 45f) return;

        // Step 5: Rotate so that this normal becomes Vector3.up
        Quaternion target = Quaternion.FromToRotation(normal, Vector3.up);

        floorCorrection = Quaternion.Slerp(floorCorrection, target, levelSmoothing);
    }

    List<Vector3> BackProject(float[] depths, int w, int h, float _fx, float _fy, float _cx, float _cy)
    {
        var points = new List<Vector3>();
        int step = Mathf.Max(1, downsample);

        for (int py = 0; py < h; py += step)
        {
            for (int px = 0; px < w; px += step)
            {
                float d = depths[py * w + px];
                if (invertDepth) d = maxDepth - d;
                if (d <= 0.1f || d > maxDepth) continue;

                float x = (px - _cx) / _fx * d;
                float y = (py - _cy) / _fy * d;
                points.Add(new Vector3(x * meshScale, -y * meshScale, d * meshScale));
            }
        }
        return points;
    }

    void BuildVisualMesh(float[] depths, int w, int h, float _fx, float _fy, float _cx, float _cy)
    {
        int step = Mathf.Max(1, downsample);
        int gridW = (w - 1) / step + 1;
        int gridH = (h - 1) / step + 1;

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        var depthVals = new List<float>();
        int[,] vmap = new int[gridH, gridW];

        for (int gy = 0; gy < gridH; gy++)
            for (int gx = 0; gx < gridW; gx++)
                vmap[gy, gx] = -1;

        for (int gy = 0; gy < gridH; gy++)
        {
            int py = Mathf.Min(gy * step, h - 1);
            for (int gx = 0; gx < gridW; gx++)
            {
                int px = Mathf.Min(gx * step, w - 1);
                float d = depths[py * w + px];
                if (invertDepth) d = maxDepth - d;
                if (d <= 0.1f || d > maxDepth) continue;

                float x = (px - _cx) / _fx * d;
                float y = (py - _cy) / _fy * d;
                vmap[gy, gx] = verts.Count;
                Vector3 raw = new Vector3(x * meshScale, -y * meshScale, d * meshScale);
                Vector3 corrected = floorCorrection * raw;
                corrected.y += currentYOffset;
                verts.Add(corrected);
                uvs.Add(new Vector2((float)px / (w - 1), 1f - (float)py / (h - 1)));
                depthVals.Add(d);
            }
        }

        for (int gy = 0; gy < gridH - 1; gy++)
        {
            for (int gx = 0; gx < gridW - 1; gx++)
            {
                int tl = vmap[gy, gx], tr = vmap[gy, gx + 1];
                int bl = vmap[gy + 1, gx], br = vmap[gy + 1, gx + 1];

                if (tl >= 0 && tr >= 0 && bl >= 0 && DC(depthVals, tl, tr, bl))
                { tris.Add(tl); tris.Add(tr); tris.Add(bl); }

                if (tr >= 0 && bl >= 0 && br >= 0 && DC(depthVals, tr, bl, br))
                { tris.Add(tr); tris.Add(br); tris.Add(bl); }
            }
        }

        if (verts.Count < 3) return;

        visualMesh.Clear();
        visualMesh.SetVertices(verts);
        visualMesh.SetUVs(0, uvs);
        visualMesh.SetTriangles(tris, 0);
        visualMesh.RecalculateNormals();
        visualMesh.RecalculateBounds();

        if (meshFilter != null && showMesh)
            meshFilter.mesh = visualMesh;
    }

    bool DC(List<float> d, int a, int b, int c)
    {
        float t = 0.5f;
        return Mathf.Abs(d[a] - d[b]) < t && Mathf.Abs(d[b] - d[c]) < t && Mathf.Abs(d[a] - d[c]) < t;
    }

    // ─── Voxel collision system with 3D greedy merge ───

    struct VoxelKey : IEquatable<VoxelKey>
    {
        public int x, y, z;
        public VoxelKey(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(VoxelKey o) => x == o.x && y == o.y && z == o.z;
        public override int GetHashCode() => x * 73856093 ^ y * 19349663 ^ z * 83492791;
    }

    // Merged box in voxel coordinates
    struct VoxelBox
    {
        public int minX, minY, minZ, maxX, maxY, maxZ;
        public int SizeX => maxX - minX + 1;
        public int SizeY => maxY - minY + 1;
        public int SizeZ => maxZ - minZ + 1;
        public int Volume => SizeX * SizeY * SizeZ;
    }

    void BuildVoxelColliders(List<Vector3> points)
    {
        foreach (var go in activeColliders)
            Destroy(go);
        activeColliders.Clear();

        if (points.Count < 10) return;

        float vs = voxelSize;

        // Step 1: Fill voxel grid
        var grid = new Dictionary<VoxelKey, int>();
        foreach (var p in points)
        {
            var key = new VoxelKey(
                Mathf.FloorToInt(p.x / vs),
                Mathf.FloorToInt(p.y / vs),
                Mathf.FloorToInt(p.z / vs)
            );
            if (grid.ContainsKey(key)) grid[key]++;
            else grid[key] = 1;
        }

        // Step 2: Filter sparse voxels
        var occupied = new HashSet<VoxelKey>();
        foreach (var kv in grid)
            if (kv.Value >= minPointsPerVoxel)
                occupied.Add(kv.Key);

        if (occupied.Count == 0) return;

        // Step 3: Full 3D greedy merge
        var boxes = GreedyMerge3D(occupied);

        if (boxes.Count == 0) return;

        // Step 4: Find the global lowest Y across all boxes (for Y=0 offset)
        float globalMinY = float.MaxValue;
        foreach (var box in boxes)
        {
            float bottomY = box.minY * vs;
            if (bottomY < globalMinY) globalMinY = bottomY;
        }
        float yOffset = -globalMinY; // shift so lowest = 0

        // Step 5: Find THE single best floor box (largest XZ area, thin, near bottom)
        int bestFloorIdx = -1;
        float bestFloorArea = 0;

        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            float area = box.SizeX * box.SizeZ;
            bool isThin = box.SizeY <= 3;
            float bottomY = box.minY * vs + yOffset;

            // Only consider boxes near the bottom (within 0.5m of ground)
            if (isThin && area >= minVoxelsForPlane && bottomY < 0.5f && area > bestFloorArea)
            {
                bestFloorArea = area;
                bestFloorIdx = i;
            }
        }

        // Step 6: Create colliders
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];

            float cx = (box.minX + box.maxX + 1) * 0.5f * vs;
            float cy = (box.minY + box.maxY + 1) * 0.5f * vs + yOffset;
            float cz = (box.minZ + box.maxZ + 1) * 0.5f * vs;

            float sx = box.SizeX * vs;
            float sy = box.SizeY * vs;
            float sz = box.SizeZ * vs;

            bool isFloor = (i == bestFloorIdx);

            if (isFloor)
            {
                sx = Mathf.Max(sx, 30f);
                sz = Mathf.Max(sz, 30f);
                FloorY = cy;
            }

            Material mat = null;
            if (showVoxels)
                mat = isFloor ? planeMat : objectMat;

            CreateBoxCollider(new Vector3(cx, cy, cz), new Vector3(sx, sy, sz), isFloor, mat);
        }

        // Step 7: Safety floor at Y = -0.05 (just below 0)
        CreateBoxCollider(
            new Vector3(0, -0.05f, 0),
            new Vector3(60f, 0.05f, 60f),
            true, null
        );

        // Store offset so visual mesh can use it
        currentYOffset = yOffset;
    }

    /// <summary>
    /// Full 3D greedy box merge. Scans X, then expands in Z, then expands in Y.
    /// Produces minimal set of axis-aligned boxes covering all occupied voxels.
    /// </summary>
    List<VoxelBox> GreedyMerge3D(HashSet<VoxelKey> occupied)
    {
        var result = new List<VoxelBox>();
        var used = new HashSet<VoxelKey>();

        // Get bounds
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        int minZ = int.MaxValue, maxZ = int.MinValue;

        foreach (var v in occupied)
        {
            if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
            if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
            if (v.z < minZ) minZ = v.z; if (v.z > maxZ) maxZ = v.z;
        }

        // Scan in Y -> Z -> X order (Y outer = merges vertical slabs well)
        for (int iy = minY; iy <= maxY; iy++)
        {
            for (int iz = minZ; iz <= maxZ; iz++)
            {
                for (int ix = minX; ix <= maxX; ix++)
                {
                    var start = new VoxelKey(ix, iy, iz);
                    if (!occupied.Contains(start) || used.Contains(start))
                        continue;

                    // Expand in +X
                    int ex = ix;
                    while (ex + 1 <= maxX)
                    {
                        var next = new VoxelKey(ex + 1, iy, iz);
                        if (occupied.Contains(next) && !used.Contains(next)) ex++;
                        else break;
                    }

                    // Expand in +Z (check full X span)
                    int ez = iz;
                    while (ez + 1 <= maxZ)
                    {
                        bool rowOk = true;
                        for (int tx = ix; tx <= ex; tx++)
                        {
                            var check = new VoxelKey(tx, iy, ez + 1);
                            if (!occupied.Contains(check) || used.Contains(check))
                            { rowOk = false; break; }
                        }
                        if (rowOk) ez++;
                        else break;
                    }

                    // Expand in +Y (check full XZ slab)
                    int ey = iy;
                    while (ey + 1 <= maxY)
                    {
                        bool slabOk = true;
                        for (int tz = iz; tz <= ez && slabOk; tz++)
                        {
                            for (int tx = ix; tx <= ex; tx++)
                            {
                                var check = new VoxelKey(tx, ey + 1, tz);
                                if (!occupied.Contains(check) || used.Contains(check))
                                { slabOk = false; break; }
                            }
                        }
                        if (slabOk) ey++;
                        else break;
                    }

                    // Mark all voxels in the merged box as used
                    for (int ty = iy; ty <= ey; ty++)
                        for (int tz = iz; tz <= ez; tz++)
                            for (int tx = ix; tx <= ex; tx++)
                                used.Add(new VoxelKey(tx, ty, tz));

                    result.Add(new VoxelBox
                    {
                        minX = ix, minY = iy, minZ = iz,
                        maxX = ex, maxY = ey, maxZ = ez
                    });
                }
            }
        }

        return result;
    }

    void CreateBoxCollider(Vector3 center, Vector3 size, bool isPlane, Material mat)
    {
        var go = new GameObject(isPlane ? "PlaneCollider" : "VoxelCollider");
        go.transform.SetParent(colliderParent.transform);
        go.transform.localPosition = center;
        go.layer = gameObject.layer;

        var box = go.AddComponent<BoxCollider>();
        box.size = size;

        if (mat != null)
        {
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.mesh = MakeCubeMesh();
            mr.material = mat;
            go.transform.localScale = size;
        }

        activeColliders.Add(go);
    }

    Mesh _cubeMesh;
    Mesh MakeCubeMesh()
    {
        if (_cubeMesh != null) return _cubeMesh;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cubeMesh = go.GetComponent<MeshFilter>().mesh;
        Destroy(go);
        return _cubeMesh;
    }

    void OnDestroy()
    {
        running = false;
        Disconnect();
        foreach (var go in activeColliders) Destroy(go);
        if (colliderParent != null) Destroy(colliderParent);
    }

    void OnGUI()
    {
        string status = connected ? "<color=green>Connected</color>" : "<color=red>Waiting...</color>";
        GUI.Label(new Rect(10, 10, 500, 25), $"[DepthMesh] {status}");
        if (connected)
        {
            float angle = Quaternion.Angle(Quaternion.identity, floorCorrection);
            GUI.Label(new Rect(10, 30, 500, 25),
                $"Depth: {depthWidth}x{depthHeight} | Colliders: {activeColliders.Count} | Floor Y: {FloorY:F2} | Tilt: {angle:F1}°");
        }
    }
}
