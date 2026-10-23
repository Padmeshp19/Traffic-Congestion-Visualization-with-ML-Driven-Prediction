using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool (Phase 1). Builds the spawnable vehicle templates in Assets/JunctionSim/Prefabs.
///
/// Every template is:
///   Root  - VehicleInfo + LaneCarController, scale 1, pivot at the bottom-centre of the vehicle
///     Model - the original model from the asset pack (still linked to it), turned so its front
///             faces the root's blue Z arrow, scaled, with its wheels at height 0.
///
/// Sizes are relative to Car2 at scale 0.18, which is how the junction scene already uses it.
///
/// Menu:
///   Tools > Junction Sim > Build Vehicle Templates      (safe to run again; it overwrites the templates)
///   Tools > Junction Sim > Preview Templates In Scene   (lines them up on RoadLane_2 for a visual check)
/// </summary>
public static class VehicleTemplateBuilder
{
    private const string OutputFolder = "Assets/JunctionSim/Prefabs";
    private const string MaterialFolder = "Assets/JunctionSim/Materials";
    private const string CarPack = "Assets/Stylized Vehicles Pack Free/Prefabs/";
    private const string BikePack = "Assets/MotorcyclePack_/MotorcyclePack_SRP_Build_/Prefab/";

    // Car2 is already in the junction scene at this scale; every other size is relative to it.
    private const string ReferenceCar = CarPack + "Car2.prefab";
    private const float ReferenceCarScale = 0.18f;

    private class Spec
    {
        public string Name;
        public string Source;
        public VehicleType Type;
        public float CarLengths;   // target length, in Car2-lengths (car 1, bike 0.5, bus 1.5 ...)
        public bool TurnAround = false;   // set true if this model ends up driving backwards
        public bool Tinted;        // body recoloured (used for the truck stand-in)
    }

    private static readonly Color TruckTint = new Color(1f, 0.55f, 0.1f);

    // ---- The templates. Change CarLengths to resize, or TurnAround = true to flip a model. ----
    private static readonly Spec[] Specs =
    {
        new Spec { Name = "Car_Car2",      Source = CarPack + "Car2.prefab",      Type = VehicleType.Car,   CarLengths = 1.00f },
        new Spec { Name = "Car_Sedan1",    Source = CarPack + "Sedan1.prefab",    Type = VehicleType.Car,   CarLengths = 1.05f },
        new Spec { Name = "Car_SportCar2", Source = CarPack + "SportCar2.prefab", Type = VehicleType.Car,   CarLengths = 1.05f },
        new Spec { Name = "Car_Jeep2",     Source = CarPack + "Jeep2.prefab",     Type = VehicleType.Car,   CarLengths = 1.05f },
        new Spec { Name = "Bus_MicroBus4", Source = CarPack + "MicroBus4.prefab", Type = VehicleType.Bus,   CarLengths = 1.50f },
        new Spec { Name = "Truck_Standin", Source = CarPack + "MicroBus4.prefab", Type = VehicleType.Truck, CarLengths = 1.60f, Tinted = true },
        new Spec { Name = "Bike_1", Source = BikePack + "SuperMotors_1_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_2", Source = BikePack + "SuperMotors_2_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_3", Source = BikePack + "SuperMotors_3_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_4", Source = BikePack + "SuperMotors_4_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_5", Source = BikePack + "SuperMotors_5_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_6", Source = BikePack + "SuperMotors_6_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_7", Source = BikePack + "SuperMotors_7_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_8", Source = BikePack + "SuperMotors_8_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
        new Spec { Name = "Bike_9", Source = BikePack + "SuperMotors_9_.prefab", Type = VehicleType.Bike, CarLengths = 0.50f },
    };

    private static float BaseSpeed(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Bike: return 3.0f;
            case VehicleType.Bus: return 2.0f;
            case VehicleType.Truck: return 1.8f;
            default: return 2.5f;   // car; same as the existing scene cars
        }
    }

    // Empty space kept in front of the vehicle, in Car2-lengths.
    private static float SafetyGapInCarLengths(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Bike: return 0.25f;
            case VehicleType.Bus:
            case VehicleType.Truck: return 0.45f;
            default: return 0.35f;
        }
    }

    // ==========================================================================================
    // Build
    // ==========================================================================================

    [MenuItem("Tools/Junction Sim/Build Vehicle Templates")]
    public static void BuildAll()
    {
        EnsureFolder(OutputFolder);
        EnsureFolder(MaterialFolder);

        float carLength = MeasureReferenceCarLength();
        if (carLength <= 0f)
        {
            Debug.LogError($"VehicleTemplateBuilder: could not measure {ReferenceCar}. Is the asset pack in the project?");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"VehicleTemplateBuilder: Car2 at scale {ReferenceCarScale} is {carLength:0.###} units long.");
        report.AppendLine("Template          Type   Length  Width  Height  Speed  Gap");

        int built = 0;
        foreach (Spec spec in Specs)
        {
            if (BuildOne(spec, carLength, report))
                built++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        report.AppendLine($"Built {built} of {Specs.Length} templates in {OutputFolder}.");
        Debug.Log(report.ToString());
        EditorUtility.DisplayDialog("Vehicle templates",
            $"Built {built} of {Specs.Length} templates in {OutputFolder}.\n\nSee the Console for sizes. " +
            "Next: Tools > Junction Sim > Preview Templates In Scene.", "OK");
    }

    private static float MeasureReferenceCarLength()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(ReferenceCar);
        if (source == null)
            return 0f;

        var root = new GameObject("__ReferenceCar");
        try
        {
            Transform model = AddModel(root.transform, source);
            FaceForward(root.transform, model, false);
            model.localScale = model.localScale * ReferenceCarScale;
            return VehicleInfo.TryMeasureLocalBounds(root.transform, out Bounds b) ? b.size.z : 0f;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static bool BuildOne(Spec spec, float carLength, StringBuilder report)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(spec.Source);
        if (source == null)
        {
            report.AppendLine($"SKIPPED {spec.Name}: cannot find {spec.Source}");
            return false;
        }

        var root = new GameObject(spec.Name);
        try
        {
            Transform model = AddModel(root.transform, source);

            int missing = model.GetComponentsInChildren<Transform>(true)
                               .Sum(t => t.GetComponents<Component>().Count(c => c == null));
            if (missing > 0)
                report.AppendLine($"WARNING {spec.Name}: the source prefab has {missing} missing script(s). " +
                                  "Open it and remove the Missing (Mono Script) component.");

            FaceForward(root.transform, model, spec.TurnAround);
            FitModel(root.transform, model, spec.CarLengths * carLength);

            if (spec.Tinted)
                TintBody(model.gameObject, $"{MaterialFolder}/{spec.Name}_Body.mat", TruckTint);

            VehicleInfo.TryMeasureLocalBounds(root.transform, out Bounds b);

            var info = root.AddComponent<VehicleInfo>();
            info.type = spec.Type;
            info.length = b.size.z;
            info.width = b.size.x;
            info.height = b.size.y;
            info.baseSpeed = BaseSpeed(spec.Type);
            info.speedVariation = 0.1f;
            info.safetyGap = SafetyGapInCarLengths(spec.Type) * carLength;

            var driver = root.AddComponent<LaneCarController>();
            driver.speed = info.baseSpeed;

            string path = $"{OutputFolder}/{spec.Name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
            if (!saved)
            {
                report.AppendLine($"FAILED to save {path}");
                return false;
            }

            report.AppendLine($"{spec.Name,-17} {spec.Type,-6} {info.length,6:0.00}  {info.width,5:0.00}  " +
                              $"{info.height,6:0.00}  {info.baseSpeed,5:0.0}  {info.safetyGap,4:0.00}" +
                              (info.width > 0.7f ? "   <- wider than a lane, check it" : ""));
            return true;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static Transform AddModel(Transform root, GameObject source)
    {
        var model = (GameObject)PrefabUtility.InstantiatePrefab(source);
        model.transform.SetParent(root, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        return model.transform;
    }

    /// <summary>
    /// Vehicles drive along their blue Z arrow. If a model is longer along X than along Z,
    /// it is lying sideways, so turn it a quarter turn. TurnAround adds a half turn.
    /// </summary>
    private static void FaceForward(Transform root, Transform model, bool turnAround)
    {
        model.localRotation = Quaternion.identity;

        if (VehicleInfo.TryMeasureLocalBounds(root, out Bounds b) && b.size.x > b.size.z * 1.2f)
            model.localRotation = Quaternion.Euler(0f, -90f, 0f);

        if (turnAround)
            model.localRotation = Quaternion.Euler(0f, 180f, 0f) * model.localRotation;
    }

    /// <summary>Scale to the target length, then put the wheels at height 0 and centre the vehicle on the root.</summary>
    private static void FitModel(Transform root, Transform model, float targetLength)
    {
        if (VehicleInfo.TryMeasureLocalBounds(root, out Bounds b) && b.size.z > 0.0001f)
            model.localScale = model.localScale * (targetLength / b.size.z);

        if (VehicleInfo.TryMeasureLocalBounds(root, out b))
            model.localPosition -= new Vector3(b.center.x, b.min.y, b.center.z);
    }

    /// <summary>Gives the body (PixelColors material) a coloured copy, leaving glass and lights alone.</summary>
    private static void TintBody(GameObject model, string materialPath, Color tint)
    {
        Material tinted = null;

        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null || materials[i].name != "PixelColors")
                    continue;

                if (tinted == null)
                    tinted = GetOrCreateMaterial(materials[i], materialPath, tint);

                materials[i] = tinted;
                changed = true;
            }

            if (changed)
                renderer.sharedMaterials = materials;
        }
    }

    private static Material GetOrCreateMaterial(Material source, string path, Color tint)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(source);
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = tint;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
    }

    // ==========================================================================================
    // Preview
    // ==========================================================================================

    [MenuItem("Tools/Junction Sim/Preview Templates In Scene")]
    public static void PreviewInScene()
    {
        List<GameObject> templates = AssetDatabase.FindAssets("t:Prefab", new[] { OutputFolder })
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Select(path => AssetDatabase.LoadAssetAtPath<GameObject>(path))
            .Where(go => go != null && go.GetComponent<VehicleInfo>() != null)
            .OrderBy(go => go.GetComponent<VehicleInfo>().type)
            .ThenBy(go => go.name)
            .ToList();

        if (templates.Count == 0)
        {
            EditorUtility.DisplayDialog("Vehicle templates",
                "No templates found. Run Tools > Junction Sim > Build Vehicle Templates first.", "OK");
            return;
        }

        // Line them up on RoadLane_2, before the stop line, facing the way traffic drives.
        Vector3 direction = Vector3.right;
        Vector3 start = new Vector3(-19f, 0.1f, 1.57f);

        GameObject lane = GameObject.Find("RoadLane_2");
        Transform stopLine = lane != null ? lane.transform.Find("Point_Junction") : null;
        Transform endPoint = lane != null ? lane.transform.Find("EndPoint") : null;
        if (stopLine != null && endPoint != null)
        {
            Vector3 along = endPoint.position - stopLine.position;
            along.y = 0f;
            if (along.sqrMagnitude > 0.0001f)
                direction = along.normalized;
            start = stopLine.position - direction * 18.5f;
        }

        var parent = new GameObject("TemplatePreview (delete me)");
        Undo.RegisterCreatedObjectUndo(parent, "Preview vehicle templates");

        Vector3 cursor = start;
        foreach (GameObject template in templates)
        {
            var info = template.GetComponent<VehicleInfo>();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(template, parent.transform);

            cursor += direction * info.HalfLength;
            Vector3 position = cursor;
            position.y = RoadHeight(cursor, start.y);
            instance.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));
            cursor += direction * (info.HalfLength + info.safetyGap + 0.2f);
        }

        Selection.activeGameObject = parent;
        Debug.Log($"VehicleTemplateBuilder: placed {templates.Count} templates on RoadLane_2 under " +
                  "'TemplatePreview (delete me)'. Check them in the Scene view, then delete that object.");
    }

    private static float RoadHeight(Vector3 point, float fallback)
    {
        // Start the ray below the flyover deck so it lands on the road surface.
        return Physics.Raycast(point + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 3f)
            ? hit.point.y
            : fallback;
    }
}