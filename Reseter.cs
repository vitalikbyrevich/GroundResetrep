using GroundReset.Compatibility.kgMarketplace;

namespace GroundReset;

public static class Reseter
{
    public static readonly int HeightmapWidth = 64;
    public static readonly int HeightmapScale = 1;
    private static List<ZDO> wards = new();
    public static List<WardSettings> wardsSettingsList = new();
    public static Stopwatch watch = new();
    
    private static Dictionary<Vector3, float> _wardCache = new();
    private static long _lastWardCacheUpdate;

    public static async Task ResetAll(bool checkIfNeed = true, bool checkWards = true, bool ranFromConsole = false)
    {
        try
        {
            await FindWards();
            await Terrains.ResetTerrains(checkWards);

            if (ranFromConsole) Console.instance.AddString("<color=green> Done </color>");
        }
        catch (Exception e)
        {
            LogError($"ResetAll failed with exception: {e}");
        }
    }

    private static async Task FindWards()
    {
        // Обновляем кэш только если прошло больше 30 секунд
        if (DateTime.Now.Ticks - _lastWardCacheUpdate < TimeSpan.TicksPerSecond * 30 && 
            _wardCache.Count > 0)
            return;

        watch.Restart();
        wards.Clear();
        
        for (var i = 0; i < wardsSettingsList.Count; i++)
        {
            var wardsSettings = wardsSettingsList[i];
            var zdos = await ZoneSystem.instance.GetWorldObjectsAsync(wardsSettings.prefabName);
            if (zdos != null)
                wards.AddRange(zdos);
        }

        PrecacheWards();
        _lastWardCacheUpdate = DateTime.Now.Ticks;

        var totalSeconds = TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds).TotalSeconds;
        LogInfo($"Wards count: {wards.Count}. Caching took {totalSeconds} seconds");
        watch.Restart();
    }
    
    public static void PrecacheWards()
    {
        var newCache = new Dictionary<Vector3, float>();
        
        foreach (var ward in wards)
        {
            try
            {
                var pos = ward.GetPosition();
                var wardSettings = wardsSettingsList.Find(s => 
                    s.prefabName.GetStableHashCode() == ward.GetPrefab());
                
                if (wardSettings.dynamicRadius)
                {
                    var radius = wardSettings.getDynamicRadius(ward);
                    newCache[pos] = radius;
                }
                else
                {
                    newCache[pos] = wardSettings.radius;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Error caching ward: {ex}");
            }
        }
        
        _wardCache = newCache;
    }

    public static Vector3 HmapToWorld(Vector3 heightmapPos, int x, int y)
    {
        var xPos = ((float)x - HeightmapWidth / 2) * HeightmapScale;
        var zPos = ((float)y - HeightmapWidth / 2) * HeightmapScale;
        return heightmapPos + new Vector3(xPos, 0f, zPos);
    }

    public static void WorldToVertex(Vector3 worldPos, Vector3 heightmapPos, out int x, out int y)
    {
        var vector3 = worldPos - heightmapPos;
        x = FloorToInt((float)(vector3.x / (double)HeightmapScale + 0.5)) + HeightmapWidth / 2;
        y = FloorToInt((float)(vector3.z / (double)HeightmapScale + 0.5)) + HeightmapWidth / 2;
    }
    
    public static bool IsInWard(Vector3 pos, float checkRadius = 0)
    {
        foreach (var wardEntry in _wardCache)
        {
            var wardPos = wardEntry.Key;
            var radius = wardEntry.Value;
            
            // Быстрый расчет расстояния
            var dx = pos.x - wardPos.x;
            var dz = pos.z - wardPos.z;
            var distanceSquared = dx * dx + dz * dz;
            var maxDistance = radius + checkRadius;
            
            if (distanceSquared <= maxDistance * maxDistance) return true;
        }
        return MarketplaceTerritorySystem.PointInTerritory(pos);
    }

    public static bool IsInWard(Vector3 zoneCenter, int w, int h) { return IsInWard(HmapToWorld(zoneCenter, w, h)); }
}