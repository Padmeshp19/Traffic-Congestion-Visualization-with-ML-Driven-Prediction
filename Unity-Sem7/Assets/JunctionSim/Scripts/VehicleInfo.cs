using UnityEngine;

public enum VehicleType
{
    Car,
    Bike,
    Bus,
    Truck
}

/// <summary>
/// Label for every spawnable vehicle template: what it is, how big it is and how it drives.
/// The junction spawner uses it to count vehicles by type and keep safe gaps between them.
///
/// Template layout (made by Tools > Junction Sim > Build Vehicle Templates):
///   Root (this component + LaneCarController, scale 1)
///     Model (the vehicle model, scaled and turned so its front faces the root's blue Z arrow)
/// The root's pivot sits at the bottom-centre of the vehicle, so placing the root on the road
/// puts the wheels exactly on the road.
/// </summary>
[DisallowMultipleComponent]
public class VehicleInfo : MonoBehaviour
{
    [Header("What this vehicle is")]
    public VehicleType type = VehicleType.Car;

    [Header("Size in world units (component menu > Measure From Model)")]
    [Tooltip("Front to back, along the blue Z arrow.")]
    public float length = 1f;
    [Tooltip("Side to side.")]
    public float width = 0.5f;
    public float height = 0.5f;

    [Header("Driving")]
    public float baseSpeed = 2.5f;
    [Tooltip("Each spawned vehicle gets a random speed within plus or minus this share of the base speed.")]
    [Range(0f, 0.5f)] public float speedVariation = 0.1f;
    [Tooltip("Empty space this vehicle keeps in front of it, between its front bumper and the next vehicle's back.")]
    public float safetyGap = 0.3f;

    public float HalfLength => length * 0.5f;

    /// <summary>Random speed around the base speed, so traffic does not look robotic.</summary>
    public float PickSpeed()
    {
        return baseSpeed * Random.Range(1f - speedVariation, 1f + speedVariation);
    }

    [ContextMenu("Measure From Model")]
    public void MeasureFromModel()
    {
        if (!TryMeasureLocalBounds(transform, out Bounds bounds))
        {
            Debug.LogWarning($"{name}: no meshes found to measure.", this);
            return;
        }

        Vector3 scale = transform.lossyScale;
        length = bounds.size.z * Mathf.Abs(scale.z);
        width = bounds.size.x * Mathf.Abs(scale.x);
        height = bounds.size.y * Mathf.Abs(scale.y);
        Debug.Log($"{name}: length {length:0.###}, width {width:0.###}, height {height:0.###}", this);
    }

    /// <summary>
    /// Box around every mesh under <paramref name="root"/>, in the root's own axes
    /// (Z = front/back, X = side to side, Y = up), ignoring the root's own scale.
    /// </summary>
    public static bool TryMeasureLocalBounds(Transform root, out Bounds bounds)
    {
        bounds = new Bounds();
        bool found = false;

        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh != null && filter.GetComponent<Renderer>() != null)
                AddMesh(root, filter.transform, filter.sharedMesh.bounds, ref bounds, ref found);
        }

        foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skinned.sharedMesh != null)
                AddMesh(root, skinned.transform, skinned.sharedMesh.bounds, ref bounds, ref found);
        }

        return found;
    }

    private static void AddMesh(Transform root, Transform meshTransform, Bounds meshBounds,
                                ref Bounds total, ref bool found)
    {
        Vector3 c = meshBounds.center;
        Vector3 e = meshBounds.extents;

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                c.x + ((i & 1) == 0 ? -e.x : e.x),
                c.y + ((i & 2) == 0 ? -e.y : e.y),
                c.z + ((i & 4) == 0 ? -e.z : e.z));

            Vector3 pointInRoot = root.InverseTransformPoint(meshTransform.TransformPoint(corner));

            if (!found)
            {
                total = new Bounds(pointInRoot, Vector3.zero);
                found = true;
            }
            else
            {
                total.Encapsulate(pointInRoot);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;

        // Vehicle footprint (cyan) and the safety gap it keeps in front (yellow).
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(new Vector3(0f, height * 0.5f, 0f), new Vector3(width, height, length));

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(new Vector3(0f, 0.01f, HalfLength + safetyGap * 0.5f),
                            new Vector3(width, 0.02f, safetyGap));
    }
}