
// ReSharper disable PossibleLossOfFraction

namespace GroundReset;

public static class Terrains
{
     public static async Task<int> ResetTerrains(bool checkWards)
    {
        var startTime = DateTime.Now;
        
        // Первое сообщение - начало процесса
        MessageHud.instance.MessageAll(MessageHud.MessageType.Center, "Внимание: начинается восстановление земли. Возможны кратковременные лаги.");
        MessageHud.instance.MessageAll(MessageHud.MessageType.TopLeft, "Идёт сброс ландшафта...");
        
        // Задержка зависит от ожидаемого объема работы
        var expectedDelay = GetExpectedDelay();
        await Task.Delay(expectedDelay);
        
        // Минимальная задержка перед началом работы (1 секунда)
        await Task.Delay(TimeSpan.FromSeconds(1));
        
        Reseter.watch.Restart();
        var zdos = await ZoneSystem.instance.GetWorldObjectsAsync(Consts.TerrCompPrefabName);
        
        if (zdos.Count == 0)
        {
            LogInfo("0 chunks have been reset. Took 0.0 seconds");
            Reseter.watch.Stop();
            
            // Задержка перед сообщением о завершении
            await Task.Delay(TimeSpan.FromSeconds(2));
            MessageHud.instance.MessageAll(MessageHud.MessageType.TopLeft, "Сброс ландшафта завершён");
            return 0;
        }
        
        LogInfo($"Found {zdos.Count} chunks to reset", insertTimestamp:true);

        // Рассчитываем общее время выполнения для плавного прогресса
        int dop = ConfigsContainer.MaxParallelism;
        dop = Math.Max(1, Math.Min(dop, 4));
        
        int totalProcessed = 0;
        var processingStartTime = DateTime.Now;

        if (ConfigsContainer.EnableProgressiveReset)
        {
            totalProcessed = await ProcessProgressive(zdos, checkWards, dop);
        }
        else
        {
            totalProcessed = await ProcessDirect(zdos, checkWards, dop);
        }

        foreach (var comp in TerrainComp.s_instances) comp.m_hmap?.Poke(false);

      //  var processingTime = DateTime.Now - processingStartTime;
        var totalSeconds = TimeSpan.FromMilliseconds(Reseter.watch.ElapsedMilliseconds).TotalSeconds;
        
        LogInfo($"{totalProcessed} chunks have been reset. Took {totalSeconds} seconds", insertTimestamp:true);
        Reseter.watch.Stop();

        // Гарантируем минимальную общую длительность процесса - 5 секунд
        var totalElapsed = DateTime.Now - startTime;
        var minDuration = TimeSpan.FromSeconds(ConfigsContainer.MinProcessDuration);
        
        if (totalElapsed < minDuration)
        {
            var remainingDelay = minDuration - totalElapsed;
            if (remainingDelay > TimeSpan.Zero)
            {
                // Показываем прогресс во время ожидания
                var progressSteps = (int)(remainingDelay.TotalSeconds / 0.5); // шаги по 0.5 секунды
                for (int i = 0; i < progressSteps; i++)
                {
                    MessageHud.instance.MessageAll(MessageHud.MessageType.TopLeft, 
                        $"Завершение... {(i + 1) * 50 / progressSteps}%");
                    await Task.Delay(TimeSpan.FromSeconds(0.5));
                }
            }
        }

        // Финальные сообщения
        MessageHud.instance.MessageAll(MessageHud.MessageType.Center, 
            "Восстановление земли завершено");
        MessageHud.instance.MessageAll(MessageHud.MessageType.TopLeft, "Сброс ландшафта завершён");
        
        return totalProcessed;
    }
     
     private static TimeSpan GetExpectedDelay()
    {
        // Базовая задержка + дополнительная в зависимости от настроек
        var baseDelay = TimeSpan.FromSeconds(ConfigsContainer.StartupDelay);
        
        // Если включено прогрессивное восстановление - меньшая задержка
        if (ConfigsContainer.EnableProgressiveReset)
        {
            return baseDelay + TimeSpan.FromSeconds(0.5);
        }
        
        return baseDelay;
    }

    private static async Task<int> ProcessProgressive(List<ZDO> zdos, bool checkWards, int dop)
    {
        int totalSaved = 0;
        int phaseSize = ConfigsContainer.ChunksPerPhase;

        for (int phase = 0; phase < zdos.Count; phase += phaseSize)
        {
            var batch = zdos.Skip(phase).Take(phaseSize).ToList();
            var saved = await ProcessBatch(batch, checkWards, dop);
            totalSaved += saved;

            // Обновляем прогресс
            if (phase + phaseSize < zdos.Count)
            {
                var progress = (int)((double)phase / zdos.Count * 100);
                MessageHud.instance.MessageAll(MessageHud.MessageType.TopLeft, 
                    $"Восстановление... {Math.Min(phase + phaseSize, zdos.Count)}/{zdos.Count} ({progress}%)");
                
                // Пауза между фазами
                await Task.Delay(TimeSpan.FromSeconds(ConfigsContainer.PhaseDelay));
            }
        }

        return totalSaved;
    }

    private static async Task<int> ProcessDirect(List<ZDO> zdos, bool checkWards, int dop)
    {
        return await ProcessBatch(zdos, checkWards, dop);
    }

    private static async Task<int> ProcessBatch(List<ZDO> batch, bool checkWards, int dop)
    {
        using var semaphore = new SemaphoreSlim(dop, dop);
        var results = new ConcurrentBag<(ZDO zdo, ChunkData data)>();
        int completed = 0;

        var tasks = batch.Select(async zdo =>
        {
            await semaphore.WaitAsync();
            try
            {
                ChunkData? newChunkData = await ResetTerrainComp(zdo, checkWards);
                if (newChunkData is not null)
                {
                    results.Add((zdo, newChunkData));
                    Interlocked.Increment(ref completed);
                }
            }
            catch (Exception ex) 
            { 
                LogWarning($"ResetTerrainComp failed for zdo {zdo.m_uid}: {ex}"); 
            }
            finally 
            { 
                semaphore.Release(); 
            }
        });

        await Task.WhenAll(tasks);
        
        // Сохранение данных
        int saved = 0;
        foreach (var (zdo, data) in results)
        {
            try
            {
                SaveData(zdo, data);
                saved++;
            }
            catch (Exception ex) 
            { 
                LogWarning($"SaveData failed for zdo {zdo.m_uid}: {ex}"); 
            }
        }
        
        return saved;
    }

    // Оптимизированный ResetTerrainComp с улучшенной производительностью
    private static async Task<ChunkData?> ResetTerrainComp(ZDO zdo, bool checkWards)
    {
        try
        {
            var divider = ConfigsContainer.Divider;
            var resetSmooth = ConfigsContainer.ResetSmoothing;
            var minHeightToSteppedReset = ConfigsContainer.MinHeightToSteppedReset;
            var zoneCenter = ZoneSystem.GetZonePos(ZoneSystem.GetZone(zdo.GetPosition()));

            ChunkData? data = LoadOldData(zdo);
            if (data == null) return null;

            var num = Reseter.HeightmapWidth + 1;
            int yieldCounter = 0;

            // Оптимизированный цикл по высотам
            for (var idx = 0; idx < data.m_modifiedHeight.Length; idx++)
            {
                if (!data.m_modifiedHeight[idx]) continue;

                // Быстрая проверка вардов
                if (checkWards)
                {
                    var h = idx / num;
                    var w = idx % num;
                    if (Reseter.IsInWard(zoneCenter, w, h)) continue;
                }

                // Обработка высоты
                data.m_levelDelta[idx] /= divider;
                if (Math.Abs(data.m_levelDelta[idx]) < minHeightToSteppedReset) 
                    data.m_levelDelta[idx] = 0;
                    
                if (resetSmooth)
                {
                    data.m_smoothDelta[idx] /= divider;
                    if (Math.Abs(data.m_smoothDelta[idx]) < minHeightToSteppedReset) 
                        data.m_smoothDelta[idx] = 0;
                }

                var hasSmoothing = resetSmooth && data.m_smoothDelta[idx] != 0;
                data.m_modifiedHeight[idx] = data.m_levelDelta[idx] != 0 || hasSmoothing;
                
                // Периодический yield для отзывчивости
                if (++yieldCounter % 500 == 0)
                    await Task.Yield();
            }

            // Обработка покраски
            yieldCounter = 0;
            for (var idx = 0; idx < Math.Min(data.m_modifiedPaint.Length, num * num); idx++)
            {
                if (!data.m_modifiedPaint[idx]) continue;
                
                var currentPaint = data.m_paintMask[idx];
                if (IsPaintIgnored(currentPaint)) continue;
                
                // Проверка условий для покраски
                if (checkWards || ConfigsContainer.ResetPaintResetLastly)
                {
                    var h = idx / num;
                    var w = idx % num;
                    var worldPos = Reseter.HmapToWorld(zoneCenter, w, h);
                    
                    if (checkWards && Reseter.IsInWard(worldPos)) continue;
                    
                    if (ConfigsContainer.ResetPaintResetLastly && 
                        data.m_modifiedHeight.Length > idx && 
                        data.m_modifiedHeight[idx]) 
                        continue;
                }
                
                data.m_modifiedPaint[idx] = false;
                data.m_paintMask[idx] = Heightmap.m_paintMaskNothing;
                
                if (++yieldCounter % 500 == 0)
                    await Task.Yield();
            }

            return data;
        }
        catch (Exception ex)
        {
            LogError($"ResetTerrainComp failed for {zdo.m_uid}: {ex}");
            return null;
        }
    }

    private static bool IsPaintIgnored(Color color) =>
        ConfigsContainer.PaintsToIgnore
            .Exists(x =>
                Abs(x.r - color.r) < ConfigsContainer.PaintsCompareTolerance &&
                Abs(x.b - color.b) < ConfigsContainer.PaintsCompareTolerance &&
                Abs(x.g - color.g) < ConfigsContainer.PaintsCompareTolerance &&
                Abs(x.a - color.a) < ConfigsContainer.PaintsCompareTolerance
            );

    private static void SaveData(ZDO zdo, ChunkData data)
    {
        var package = new ZPackage();
        package.Write(1);
        package.Write(data.m_operations);
        package.Write(data.m_lastOpPoint);
        package.Write(data.m_lastOpRadius);
        package.Write(data.m_modifiedHeight.Length);
        for (var index = 0; index < data.m_modifiedHeight.Length; ++index)
        {
            package.Write(data.m_modifiedHeight[index]);
            if (data.m_modifiedHeight[index])
            {
                package.Write(data.m_levelDelta[index]);
                package.Write(data.m_smoothDelta[index]);
            }
        }

        package.Write(data.m_modifiedPaint.Length);
        for (var index = 0; index < data.m_modifiedPaint.Length; ++index)
        {
            package.Write(data.m_modifiedPaint[index]);
            if (data.m_modifiedPaint[index])
            {
                package.Write(data.m_paintMask[index].r);
                package.Write(data.m_paintMask[index].g);
                package.Write(data.m_paintMask[index].b);
                package.Write(data.m_paintMask[index].a);
            }
        }

        var bytes = Utils.Compress(package.GetArray());
        zdo.Set(ZDOVars.s_TCData, bytes);
    }

    private static ChunkData? LoadOldData(ZDO zdo)
    {
        var chunkData = new ChunkData();
        var byteArray = zdo.GetByteArray(ZDOVars.s_TCData);
        if (byteArray == null)
        {
            LogWarning("ByteArray is null, aborting chunk load");
            return null;
        }

        var zPackage = new ZPackage(Utils.Decompress(byteArray));
        zPackage.ReadInt();
        chunkData.m_operations = zPackage.ReadInt();
        chunkData.m_lastOpPoint = zPackage.ReadVector3();
        chunkData.m_lastOpRadius = zPackage.ReadSingle();
        var num1 = zPackage.ReadInt();
        if (num1 != chunkData.m_modifiedHeight.Length)
        {
            LogWarning("Terrain data load error, height array missmatch");
            return null;
        }

        //ok
        for (var index = 0; index < num1; ++index)
        {
            chunkData.m_modifiedHeight[index] = zPackage.ReadBool();
            if (chunkData.m_modifiedHeight[index])
            {
                chunkData.m_levelDelta[index] = zPackage.ReadSingle();
                chunkData.m_smoothDelta[index] = zPackage.ReadSingle();
            } else
            {
                chunkData.m_levelDelta[index] = 0.0f;
                chunkData.m_smoothDelta[index] = 0.0f;
            }
        }

        var num2 = zPackage.ReadInt();

        if (num2 != chunkData.m_modifiedPaint.Length)
        {
            LogWarning($"Terrain data load error, paint array missmatch, num2={num2}, modifiedPaint.Length={chunkData.m_modifiedPaint.Length}, paintMask.Length={chunkData.m_paintMask.Length}");
            num2 = Max(num2, chunkData.m_modifiedPaint.Length, chunkData.m_paintMask.Length);
            if(chunkData.m_modifiedPaint.Length != num2) Array.Resize(ref chunkData.m_modifiedPaint, num2);
            if(chunkData.m_paintMask.Length != num2)     Array.Resize(ref chunkData.m_paintMask, num2);
        }

        for (var index = 0; index < num2; ++index)
        {
            chunkData.m_modifiedPaint[index] = zPackage.ReadBool();
            if (chunkData.m_modifiedPaint[index])
                chunkData.m_paintMask[index] = new Color
                {
                    r = zPackage.ReadSingle(),
                    g = zPackage.ReadSingle(),
                    b = zPackage.ReadSingle(),
                    a = zPackage.ReadSingle()
                };
            else chunkData.m_paintMask[index] = Color.black;
        }

        var flag_copyColor = num2 == Reseter.HeightmapWidth * Reseter.HeightmapWidth;

        if (flag_copyColor)
        {
            var colorArray = new Color[chunkData.m_paintMask.Length];
            chunkData.m_paintMask.CopyTo(colorArray, 0);
            var flagArray = new bool[chunkData.m_modifiedPaint.Length];
            chunkData.m_modifiedPaint.CopyTo(flagArray, 0);
            var num3 = Reseter.HeightmapWidth + 1;
            for (var index1 = 0; index1 < chunkData.m_paintMask.Length; ++index1)
            {
                var num4 = index1 / num3;
                var num5 = (index1 + 1) / num3;
                var index2 = index1 - num4;
                if (num4 == Reseter.HeightmapWidth)
                    index2 -= Reseter.HeightmapWidth;
                if (index1 > 0 && (index1 - num4) % Reseter.HeightmapWidth == 0 && (index1 + 1 - num5) % Reseter.HeightmapWidth == 0) --index2;
                chunkData.m_paintMask[index1] = colorArray[index2];
                chunkData.m_modifiedPaint[index1] = flagArray[index2];
            }
        }

        // LogInfo(debugSb.ToString());
        return chunkData;
    }
}