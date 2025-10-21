using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

[Serializable]
public class Vec3 { public float x; public float y; public float z; }

[Serializable]
public class HandData {
    public Vec3[] landmarks;
    public bool grab;
}

public class HandReceiver : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 5005;
    public bool logPackets = false;

    [Header("Visualización")]
    public float sphereSize = 0.75f;
    public float boneRadius = 0.2f;
    public float scaleXY = 6.0f;
    public float scaleZ = 6.0f;
    public Vector3 offset = new Vector3(0, 0, 0.5f);

    [Header("Suavizado")]
    public float smoothingSpeed = 20f;

    [Header("Colisión")]
    public bool enableCollisions = true;

    [Header("Interacción")]
    public float grabRadius = 0.1f;
    public LayerMask grabbableLayer;
    private bool _isGrabbing = false;
    private GameObject _grabbedObject = null;
    private Rigidbody _grabbedRigidbody = null;
    private Vector3 _grabbedOffset;

    // Internos
    private UdpClient _udp;
    private IPEndPoint _remoteEP;
    private GameObject[] _spheres;
    private Rigidbody[] _sphereRigidbodies;
    private GameObject[] _bones;
    private Vector3[] _prevPositions;
    private bool hasData = false;

    private static readonly string[] Names = {
        "WRIST",
        "THUMB_CMC","THUMB_MCP","THUMB_IP","THUMB_TIP",
        "INDEX_MCP","INDEX_PIP","INDEX_DIP","INDEX_TIP",
        "MIDDLE_MCP","MIDDLE_PIP","MIDDLE_DIP","MIDDLE_TIP",
        "RING_MCP","RING_PIP","RING_DIP","RING_TIP",
        "PINKY_MCP","PINKY_PIP","PINKY_DIP","PINKY_TIP"
    };

    private static readonly int[,] Connections = {
        {0,1},{1,2},{2,3},{3,4},
        {0,5},{5,6},{6,7},{7,8},
        {0,9},{9,10},{10,11},{11,12},
        {0,13},{13,14},{14,15},{15,16},
        {0,17},{17,18},{18,19},{19,20}
    };

    void Awake()
    {
        _spheres = new GameObject[21];
        _sphereRigidbodies = new Rigidbody[21];
        _prevPositions = new Vector3[21];

        Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");

        for (int i = 0; i < 21; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = $"LM_{i}_{Names[i]}";
            s.transform.SetParent(transform);
            s.transform.localScale = Vector3.one * sphereSize;

            var rend = s.GetComponent<Renderer>();
            if (urpShader != null)
                rend.material = new Material(urpShader);
            rend.material.color = Color.red;

            if (enableCollisions)
            {
                var rb = s.AddComponent<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = true;
                _sphereRigidbodies[i] = rb;
            }
            else
            {
                Destroy(s.GetComponent<Collider>());
            }

            _spheres[i] = s;
            _prevPositions[i] = s.transform.localPosition;
        }

        _bones = new GameObject[Connections.GetLength(0)];
        for (int i = 0; i < _bones.Length; i++)
        {
            var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = $"Bone_{i}";
            cyl.transform.SetParent(transform);
            Destroy(cyl.GetComponent<Collider>());

            var rend = cyl.GetComponent<Renderer>();
            if (urpShader != null)
                rend.material = new Material(urpShader);
            rend.material.color = Color.white;

            cyl.transform.localScale = Vector3.zero;
            _bones[i] = cyl;
        }
    }

    void Start()
    {
        _udp = new UdpClient(listenPort);
        _remoteEP = new IPEndPoint(IPAddress.Any, 0);
        Debug.Log($"Escuchando UDP en puerto {listenPort}");
    }

    void Update()
    {
        try
        {
            while (_udp != null && _udp.Available > 0)
            {
                byte[] data = _udp.Receive(ref _remoteEP);
                string json = Encoding.UTF8.GetString(data);
                if (logPackets) Debug.Log(json);

                var hand = JsonUtility.FromJson<HandData>(json);
                if (hand == null)
                {
                    Debug.LogWarning("Hand data null");
                    continue;
                }

                // Rellenar a 21 si llega menos (fallback)
                if (hand.landmarks == null)
                {
                    Debug.LogWarning("No landmarks in packet");
                    continue;
                }
                if (hand.landmarks.Length < 21)
                {
                    Vec3[] filled = new Vec3[21];
                    for (int i = 0; i < 21; i++)
                    {
                        if (i < hand.landmarks.Length)
                            filled[i] = hand.landmarks[i];
                        else
                            filled[i] = hand.landmarks[hand.landmarks.Length - 1]; // repetir último
                    }
                    hand.landmarks = filled;
                }

                hasData = true;

                Vector3[] rawLocal = new Vector3[21];
                for (int i = 0; i < 21; i++)
                    rawLocal[i] = MapToLocalRaw(hand.landmarks[i]);

                float alpha = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);

                for (int i = 0; i < 21; i++)
                {
                    Vector3 smoothed = Vector3.Lerp(_prevPositions[i], rawLocal[i], alpha);

                    if (enableCollisions && _sphereRigidbodies[i] != null)
                        _sphereRigidbodies[i].MovePosition(smoothed);
                    else
                        _spheres[i].transform.localPosition = smoothed;

                    _prevPositions[i] = smoothed;
                }

                for (int i = 0; i < _bones.Length; i++)
                {
                    int a = Connections[i, 0];
                    int b = Connections[i, 1];
                    UpdateBone(_bones[i], _prevPositions[a], _prevPositions[b]);
                }

                // --- Interacción con objetos ---
                Vector3 indexTipPos = _spheres[8].transform.position;

                if (hand.grab && !_isGrabbing)
                {
                    Collider[] colliders = Physics.OverlapSphere(indexTipPos, grabRadius, grabbableLayer);
                    if (colliders.Length > 0)
                    {
                        _grabbedObject = colliders[0].gameObject;
                        _grabbedRigidbody = _grabbedObject.GetComponent<Rigidbody>();

                        if (_grabbedRigidbody)
                        {
                            _grabbedRigidbody.useGravity = false;
                            _grabbedRigidbody.isKinematic = true;
                        }

                        _grabbedOffset = _grabbedObject.transform.position - indexTipPos;
                        _isGrabbing = true;
                    }
                }
                else if (!hand.grab && _isGrabbing)
                {
                    if (_grabbedRigidbody)
                    {
                        _grabbedRigidbody.isKinematic = false;
                        _grabbedRigidbody.useGravity = true;
                    }
                    _grabbedObject = null;
                    _grabbedRigidbody = null;
                    _isGrabbing = false;
                }

                if (_isGrabbing && _grabbedObject)
                {
                    _grabbedObject.transform.position = indexTipPos + _grabbedOffset;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"UDP error: {e.Message}");
        }
    }

    private Vector3 MapToLocalRaw(Vec3 v)
{
    float x = (v.x - 0.5f) * scaleXY + offset.x;
    float y = (0.5f - v.y) * scaleXY + offset.y;
    float z = v.z * scaleZ + offset.z;   // ✅ sin el signo menos
    return new Vector3(x, y, z);
}


    private void UpdateBone(GameObject cyl, Vector3 start, Vector3 end)
    {
        Vector3 dir = end - start;
        float length = dir.magnitude;

        if (!hasData || length < 0.001f)
        {
            cyl.transform.localScale = Vector3.zero;
            return;
        }

        float adjustedLength = Mathf.Max(0.01f, length - sphereSize);

        Vector3 mid = (start + end) / 2f;
        cyl.transform.localPosition = mid;
        cyl.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir.normalized);
        cyl.transform.localScale = new Vector3(boneRadius, adjustedLength / 2f, boneRadius);
    }

    private void OnDestroy()
    {
        if (_udp != null)
        {
            _udp.Close();
            _udp = null;
        }
    }
}


