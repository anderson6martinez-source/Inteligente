using System;
using System.Collections.Generic;
using System.Text;
using Engine;
using Engine.Graphics;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game
{
    // Indigena inteligente:
    //  - 10 slots de inventario (IInventory propio)
    //  - recoge items del suelo (pickables) que se le acerquen o que vea cerca
    //  - abre puertas/trampillas/portones que NO sean de hierro cuando lo bloquean
    //  - lanza el item si es lanzable (lista ThrowableKeys); si no es lanzable NO lo lanza
    //  - si recoge garrotes, machetes, etc. los equipa y los usa para pelear
    //  - rompe bloques que lo bloquean, MUY lento (por tiempo, poco a poco)
    //  - el item activo se dibuja en la mano como al jugador (ComponentMiner.Inventory = este componente)
    //  - USA ARMADURAS: si recoge ropa/armadura (ClothingBlock) se la pone sola (la mejor de cada slot:
    //    cabeza, torso, piernas, pies), recibe menos dano y se le ve puesta en el cuerpo
    //
    // SIN COMPILAR: los nombres de API salen de tus DLL 2.4. Si el compilador marca algo,
    // pasame el error y lo ajusto. Los puntos mas dudosos estan marcados con [API?].
    public class ComponentIndigenaInteligente : ComponentBehavior, IUpdateable, IInventory
    {
        // ---------------- Ajustes ----------------
        private const int SlotsTotal = 10;

        // recoger
        private const float PickupSearchRadius = 8f;   // a que distancia "ve" items
        private const float PickupRange = 1.6f;        // a que distancia los recoge
        private const float PickupScanInterval = 0.4f; // cada cuanto busca (seg)

        // lanzar
        private const float ThrowMinDistance = 5f;
        private const float ThrowMaxDistance = 18f;
        private const float ThrowInterval = 1.6f;
        private const float ThrowSpeed = 24f;

        // atascado / puertas / romper
        private const float StuckSeconds = 1.0f;       // tiempo quieto antes de considerarse atascado
        private const float DoorRetrySeconds = 1.2f;   // cada cuanto reintenta abrir una puerta
        private const float BreakSecondsPerResilience = 10f;  // MUY lento (el jugador rompe ~1 de resilencia en ~1 s)
        private const float BreakMinSeconds = 6f;
        private const float BreakMaxSeconds = 120f;

        // armaduras
        private const int ArmorSlotsCount = 4;               // cabeza, torso, piernas, pies
        private const float ArmorReductionPerPoint = 0.10f;  // cada punto de ArmorProtection = 10% menos dano (por pieza)
        private const float ArmorMaxReduction = 0.80f;       // tope de reduccion total
        private const float ArmorRefreshSeconds = 20f;       // repinta la textura por si el celular la pierde

        // Tipos de bloque que cuentan como "lanzables" (parte del nombre de la clase del bloque).
        // Si el item NO coincide, NO se lanza. Agrega los que quieras.
        private static readonly string[] ThrowableKeys = { "spear", "snowball", "egg" };

        // ---------------- Estado ----------------
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemPickables m_subsystemPickables;
        private SubsystemProjectiles m_subsystemProjectiles;
        private SubsystemBlockBehaviors m_subsystemBlockBehaviors;
        private SubsystemTime m_subsystemTime;
        private ComponentCreature m_componentCreature;
        private ComponentMiner m_componentMiner;
        private ComponentChaseBehavior m_componentChase;   // opcional

        private readonly int[] m_slotValues = new int[SlotsTotal];
        private readonly int[] m_slotCounts = new int[SlotsTotal];
        private int m_activeSlot;

        // armadura puesta
        private ComponentCreatureModel m_model;
        private Texture2D m_baseSkin;
        private RenderTarget2D m_armorTexture;
        private float m_baseResilience;
        private bool m_armorInit;
        private bool m_armorDirty = true;
        private bool m_armorWorn;
        private bool m_visualFailed;
        private double m_nextArmorRefresh;

        private float m_importance;
        private double m_time;
        private double m_nextScan;
        private double m_nextThrowTime;
        private double m_nextDoorTry;

        private float m_stuckTime;
        private Point3 m_breakCell = new Point3(int.MinValue, 0, 0);
        private float m_breakProgress;

        public override float ImportanceLevel { get { return m_importance; } }
        public int UpdateOrder { get { return 0; } }
        public float FloatUpdateOrder { get { return 0f; } }

        // ================= IInventory: 10 slots =================
        Project IInventory.Project { get { return Project; } }
        public int SlotsCount { get { return SlotsTotal; } }
        public int VisibleSlotsCount { get { return SlotsTotal; } set { } }

        public int ActiveSlotIndex
        {
            get { return m_activeSlot; }
            set { if (value >= 0 && value < SlotsTotal) m_activeSlot = value; }
        }

        private static bool ValidSlot(int i) { return i >= 0 && i < SlotsTotal; }

        public int GetSlotValue(int slotIndex)
        {
            return (ValidSlot(slotIndex) && m_slotCounts[slotIndex] > 0) ? m_slotValues[slotIndex] : 0;
        }

        public int GetSlotCount(int slotIndex)
        {
            return ValidSlot(slotIndex) ? m_slotCounts[slotIndex] : 0;
        }

        private static int MaxStacking(int value)
        {
            return BlocksManager.Blocks[Terrain.ExtractContents(value)].MaxStacking;
        }

        public int GetSlotCapacity(int slotIndex, int value)
        {
            if (!ValidSlot(slotIndex)) return 0;
            if (m_slotCounts[slotIndex] <= 0) return MaxStacking(value);
            if (m_slotValues[slotIndex] == value) return Math.Max(0, MaxStacking(value) - m_slotCounts[slotIndex]);
            return 0;
        }

        public int GetSlotProcessCapacity(int slotIndex, int value) { return 0; }

        public void AddSlotItems(int slotIndex, int value, int count)
        {
            if (!ValidSlot(slotIndex) || count <= 0) return;
            if (m_slotCounts[slotIndex] <= 0)
            {
                m_slotValues[slotIndex] = value;
                m_slotCounts[slotIndex] = count;
            }
            else if (m_slotValues[slotIndex] == value)
            {
                m_slotCounts[slotIndex] += count;
            }
            m_armorDirty = true;
        }

        public void ProcessSlotItems(int slotIndex, int value, int count, int processCount,
                                     out int processedValue, out int processedCount)
        {
            processedValue = 0;
            processedCount = 0;
        }

        public int RemoveSlotItems(int slotIndex, int count)
        {
            if (!ValidSlot(slotIndex) || count <= 0 || m_slotCounts[slotIndex] <= 0) return 0;
            int removed = Math.Min(count, m_slotCounts[slotIndex]);
            m_armorDirty = true;
            m_slotCounts[slotIndex] -= removed;
            if (m_slotCounts[slotIndex] <= 0)
            {
                m_slotCounts[slotIndex] = 0;
                m_slotValues[slotIndex] = 0;
            }
            return removed;
        }

        public void DropAllItems(Vector3 position)
        {
            if (m_subsystemPickables == null) return;
            for (int i = 0; i < SlotsTotal; i++)
            {
                if (m_slotCounts[i] > 0 && m_slotValues[i] != 0)
                {
                    m_subsystemPickables.AddPickable(m_slotValues[i], m_slotCounts[i], position, Vector3.Zero, null);
                }
                m_slotValues[i] = 0;
                m_slotCounts[i] = 0;
            }
        }

        // mete items: primero apila en slots iguales, luego en vacios. Devuelve cuantos entraron.
        private int Collect(int value, int count)
        {
            int added = 0;
            for (int i = 0; i < SlotsTotal && added < count; i++)
            {
                int cap = GetSlotCapacity(i, value);
                if (cap > 0 && m_slotCounts[i] > 0)
                {
                    int n = Math.Min(cap, count - added);
                    AddSlotItems(i, value, n);
                    added += n;
                }
            }
            for (int i = 0; i < SlotsTotal && added < count; i++)
            {
                if (m_slotCounts[i] <= 0)
                {
                    int n = Math.Min(MaxStacking(value), count - added);
                    AddSlotItems(i, value, n);
                    added += n;
                }
            }
            return added;
        }

        private bool CanAccept(int value)
        {
            for (int i = 0; i < SlotsTotal; i++)
            {
                if (m_slotCounts[i] <= 0) return true;
                if (m_slotValues[i] == value && GetSlotCapacity(i, value) > 0) return true;
            }
            return false;
        }

        // ================= Carga / guardado =================
        public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>(true);
            m_subsystemProjectiles = Project.FindSubsystem<SubsystemProjectiles>(true);
            m_subsystemBlockBehaviors = Project.FindSubsystem<SubsystemBlockBehaviors>(true);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(true);
            m_componentCreature = Entity.FindComponent<ComponentCreature>(true);
            m_componentMiner = Entity.FindComponent<ComponentMiner>(true);
            m_componentChase = Entity.FindComponent<ComponentChaseBehavior>(false);
            m_model = m_componentCreature.ComponentCreatureModel;

            // Inventario guardado: "valor:cantidad,valor:cantidad,..." (10 entradas)
            string saved = valuesDictionary.GetValue<string>("Slots", "");   // [API?]
            if (!string.IsNullOrEmpty(saved))
            {
                string[] parts = saved.Split(',');
                for (int i = 0; i < parts.Length && i < SlotsTotal; i++)
                {
                    string[] vc = parts[i].Split(':');
                    int v, c;
                    if (vc.Length == 2 && int.TryParse(vc[0], out v) && int.TryParse(vc[1], out c) && c > 0)
                    {
                        m_slotValues[i] = v;
                        m_slotCounts[i] = c;
                    }
                }
            }

            // El modelo humano dibuja en la mano el item que el minero tiene activo.
            m_componentMiner.Inventory = this;
            m_armorDirty = true;
        }

        public override void Dispose()
        {
            base.Dispose();                                   // [API?] si no compila, borra este metodo
            if (m_armorTexture != null) { m_armorTexture.Dispose(); m_armorTexture = null; }
        }

        public override void Save(ValuesDictionary valuesDictionary, EntityToIdMap entityToIdMap)
        {
            base.Save(valuesDictionary, entityToIdMap);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < SlotsTotal; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(m_slotValues[i]).Append(':').Append(m_slotCounts[i]);
            }
            valuesDictionary.SetValue<string>("Slots", sb.ToString());       // [API?]
        }

        // ================= Update =================
        public void Update(float dt)
        {
            if (m_componentMiner.Inventory != this) m_componentMiner.Inventory = this;
            if (m_componentCreature.ComponentHealth.Health <= 0f) { m_importance = 0f; return; }

            m_time = m_subsystemTime.GameTime;
            ComponentCreature target = m_componentChase != null ? m_componentChase.Target : null;

            if (target != null) CombatTick(target);
            else EquipIdle();

            PickupTick(target);
            StuckTick(dt);
            ArmorTick();
        }

        // ---------- Elegir que lleva en la mano ----------
        private float MeleePower(int value)
        {
            if (value == 0) return 0f;
            return BlocksManager.Blocks[Terrain.ExtractContents(value)].GetMeleePower(value);   // [API?]
        }

        private int FindBestMeleeSlot()
        {
            int best = -1;
            float bestPower = 1.2f;    // por encima de un bloque/item normal
            for (int i = 0; i < SlotsTotal; i++)
            {
                if (m_slotCounts[i] <= 0) continue;
                if (IsClothing(m_slotValues[i])) continue;
                float p = MeleePower(m_slotValues[i]);
                if (p > bestPower) { bestPower = p; best = i; }
            }
            return best;
        }

        private static bool IsThrowable(int value)
        {
            if (value == 0) return false;
            string name = BlocksManager.Blocks[Terrain.ExtractContents(value)].GetType().Name.ToLowerInvariant();
            for (int i = 0; i < ThrowableKeys.Length; i++)
                if (name.Contains(ThrowableKeys[i])) return true;
            return false;
        }

        private int FindThrowableSlot()
        {
            for (int i = 0; i < SlotsTotal; i++)
                if (m_slotCounts[i] > 0 && IsThrowable(m_slotValues[i])) return i;
            return -1;
        }

        private void EquipIdle()
        {
            m_componentCreature.ComponentCreatureModel.AimHandAngleOrder = 0f;
            int best = FindBestMeleeSlot();
            if (best >= 0) { m_activeSlot = best; return; }
            if (m_slotCounts[m_activeSlot] > 0) return;    // ya lleva algo en la mano
            for (int i = 0; i < SlotsTotal; i++)
                if (m_slotCounts[i] > 0 && !IsClothing(m_slotValues[i])) { m_activeSlot = i; return; }
        }

        // ---------- Pelea: equipa arma y lanza lo lanzable ----------
        private void CombatTick(ComponentCreature target)
        {
            ComponentCreatureModel model = m_componentCreature.ComponentCreatureModel;
            Vector3 from = model.EyePosition;
            ComponentBody tb = target.ComponentBody;
            Vector3 to = tb.Position + new Vector3(0f, tb.StanceBoxSize.Y * 0.6f, 0f);
            float dist = Vector3.Distance(from, to);

            int throwSlot = FindThrowableSlot();
            if (throwSlot >= 0 && dist >= ThrowMinDistance && dist <= ThrowMaxDistance)
            {
                m_activeSlot = throwSlot;            // se dibuja en la mano
                model.AimHandAngleOrder = 1.4f;
                if (m_time >= m_nextThrowTime)
                {
                    Vector3 dir = Vector3.Normalize(to - from);
                    dir.Y += dist * 0.012f;          // compensa la caida
                    dir = Vector3.Normalize(dir);
                    int value = m_slotValues[throwSlot];
                    if (RemoveSlotItems(throwSlot, 1) > 0)
                    {
                        m_subsystemProjectiles.FireProjectile(
                            value, from + dir * 0.6f, dir * ThrowSpeed, Vector3.Zero, m_componentCreature);
                    }
                    m_nextThrowTime = m_time + ThrowInterval;
                }
                return;
            }

            model.AimHandAngleOrder = 0f;
            int best = FindBestMeleeSlot();
            if (best >= 0) m_activeSlot = best;      // garrote/machete/lo que tenga mejor
        }

        // ---------- Armaduras ----------
        private static bool IsClothing(int value)
        {
            return value != 0 && Terrain.ExtractContents(value) == ClothingBlock.Index;
        }

        private static ClothingData GetClothing(int value)
        {
            if (!IsClothing(value)) return null;
            return ClothingBlock.GetClothingData(value);      // [API?]
        }

        private void ArmorTick()
        {
            if (m_model == null) return;
            if (!m_armorInit)
            {
                m_armorInit = true;
                m_baseResilience = m_componentCreature.ComponentHealth.AttackResilience;   // [API?]
                m_baseSkin = m_model.TextureOverride;                                      // [API?]
            }
            bool refresh = m_armorWorn && m_time >= m_nextArmorRefresh;
            if (!m_armorDirty && !refresh) return;
            m_armorDirty = false;
            m_nextArmorRefresh = m_time + ArmorRefreshSeconds;
            RebuildArmor();
        }

        // Se pone la mejor pieza de cada slot (cabeza/torso/piernas/pies) de las que lleve en el inventario.
        private void RebuildArmor()
        {
            ClothingData[] best = new ClothingData[ArmorSlotsCount];
            for (int i = 0; i < SlotsTotal; i++)
            {
                if (m_slotCounts[i] <= 0) continue;
                ClothingData data = GetClothing(m_slotValues[i]);
                if (data == null) continue;
                int s = (int)data.Slot;
                if (s < 0 || s >= ArmorSlotsCount) continue;
                if (best[s] == null || data.ArmorProtection > best[s].ArmorProtection) best[s] = data;
            }

            float reduction = 0f;
            List<ClothingData> worn = new List<ClothingData>();
            for (int s = 0; s < ArmorSlotsCount; s++)
            {
                if (best[s] == null) continue;
                worn.Add(best[s]);
                reduction += best[s].ArmorProtection * ArmorReductionPerPoint;
            }
            reduction = MathUtils.Clamp(reduction, 0f, ArmorMaxReduction);

            // menos dano = mas resistencia al ataque (Health divide el dano entre AttackResilience)
            m_componentCreature.ComponentHealth.AttackResilience = m_baseResilience / (1f - reduction);   // [API?]
            m_armorWorn = worn.Count > 0;
            ApplyArmorSkin(worn);
        }

        // Pinta la skin del indigena + las texturas de la armadura (mismo UV que la skin) en una textura nueva.
        private void ApplyArmorSkin(List<ClothingData> worn)
        {
            if (m_baseSkin == null || m_visualFailed) return;
            try
            {
                if (worn.Count == 0)
                {
                    m_model.TextureOverride = m_baseSkin;
                    return;
                }

                worn.Sort(delegate (ClothingData a, ClothingData b) { return a.Layer.CompareTo(b.Layer); });

                int w = m_baseSkin.Width;
                int h = m_baseSkin.Height;
                if (m_armorTexture == null)
                    m_armorTexture = new RenderTarget2D(w, h, 1, ColorFormat.Rgba8888, DepthFormat.None);   // [API?]

                Rectangle scissor = Display.ScissorRectangle;
                RenderTarget2D previous = Display.RenderTarget;
                try
                {
                    Display.RenderTarget = m_armorTexture;
                    Display.Clear(Vector4.Zero, 1f);                                                     // [API?]

                    PrimitivesRenderer2D renderer = new PrimitivesRenderer2D();
                    TexturedBatch2D skin = renderer.TexturedBatch(m_baseSkin, false, 0,
                        DepthStencilState.None, RasterizerState.CullNoneScissor, BlendState.AlphaBlend, SamplerState.PointClamp);   // [API?]
                    skin.QueueQuad(Vector2.Zero, new Vector2(w, h), 0f, Vector2.Zero, Vector2.One, Color.White);

                    for (int i = 0; i < worn.Count; i++)
                    {
                        if (worn[i].Texture == null) continue;                                          // [API?]
                        TexturedBatch2D piece = renderer.TexturedBatch(worn[i].Texture, false, i + 1,
                            DepthStencilState.None, RasterizerState.CullNoneScissor, BlendState.AlphaBlend, SamplerState.PointClamp);
                        piece.QueueQuad(Vector2.Zero, new Vector2(w, h), 0f, Vector2.Zero, Vector2.One, Color.White);
                    }
                    renderer.Flush();                                                                    // [API?]
                }
                finally
                {
                    Display.RenderTarget = previous;
                    Display.ScissorRectangle = scissor;
                }
                m_model.TextureOverride = m_armorTexture;
            }
            catch (Exception)
            {
                // si el dibujo falla, se queda con la skin normal (la proteccion sigue funcionando)
                m_visualFailed = true;
                m_model.TextureOverride = m_baseSkin;
            }
        }

        // ---------- Recoger items ----------
        private void PickupTick(ComponentCreature target)
        {
            if (m_time < m_nextScan) return;
            m_nextScan = m_time + PickupScanInterval;

            ComponentBody body = m_componentCreature.ComponentBody;
            Vector3 pos = body.Position;

            Pickable nearest = null;
            float bestD2 = PickupSearchRadius * PickupSearchRadius;
            foreach (Pickable p in m_subsystemPickables.Pickables)
            {
                if (p.ToRemove || p.Count <= 0) continue;
                if (!CanAccept(p.Value)) continue;
                float d2 = Vector3.DistanceSquared(pos, p.Position);
                if (d2 < bestD2) { bestD2 = d2; nearest = p; }
            }

            if (nearest == null) { m_importance = 0f; return; }

            if (bestD2 <= PickupRange * PickupRange)
            {
                int added = Collect(nearest.Value, nearest.Count);
                nearest.Count -= added;
                if (nearest.Count <= 0) nearest.ToRemove = true;
                m_importance = 0f;
                return;
            }

            // Sin objetivo de pelea, este comportamiento toma el control y va por el item.
            m_importance = (target == null) ? 8f : 0f;
            if (IsActive && target == null)
            {
                m_componentCreature.ComponentPathfinding.SetDestination(     // [API?]
                    nearest.Position, 0.9f, 1.0f, 0, false, true, false, null);
            }
        }

        // ---------- Atascado: abrir puerta o romper bloque (muy lento) ----------
        private static bool IsOpenableNonIron(int contents)
        {
            if (contents == 0) return false;
            string n = BlocksManager.Blocks[contents].GetType().Name.ToLowerInvariant();
            return (n.Contains("door") || n.Contains("gate")) && !n.Contains("iron");
        }

        private void ResetBreak()
        {
            m_breakCell = new Point3(int.MinValue, 0, 0);
            m_breakProgress = 0f;
        }

        private void StuckTick(float dt)
        {
            ComponentBody body = m_componentCreature.ComponentBody;
            Vector3? dest = m_componentCreature.ComponentPathfinding.Destination;      // [API?]
            bool wantsToMove = dest.HasValue && Vector3.Distance(body.Position, dest.Value) > 1.8f;

            Vector3 v = body.Velocity;
            float hSpeed = (float)Math.Sqrt(v.X * v.X + v.Z * v.Z);

            if (wantsToMove && hSpeed < 0.12f) m_stuckTime += dt;
            else { m_stuckTime = 0f; ResetBreak(); return; }

            if (m_stuckTime < StuckSeconds) return;

            Vector3 d = dest.Value - body.Position;
            d.Y = 0f;
            if (d.LengthSquared() < 0.0001f) return;
            d = Vector3.Normalize(d);

            Vector3 ahead = body.Position + d * 0.9f;
            int x = Terrain.ToCell(ahead.X);
            int z = Terrain.ToCell(ahead.Z);
            int y0 = Terrain.ToCell(body.Position.Y + 0.1f);

            // 1) puertas que no sean de hierro
            for (int dy = 0; dy <= 1; dy++)
            {
                int value = m_subsystemTerrain.Terrain.GetCellValue(x, y0 + dy, z);
                int contents = Terrain.ExtractContents(value);
                if (IsOpenableNonIron(contents))
                {
                    if (m_time >= m_nextDoorTry)
                    {
                        m_nextDoorTry = m_time + DoorRetrySeconds;
                        TryInteract(x, y0 + dy, z, value, contents);
                    }
                    return;
                }
            }

            // 2) bloque que estorba: lo va rompiendo poco a poco
            for (int dy = 0; dy <= 1; dy++)
            {
                int value = m_subsystemTerrain.Terrain.GetCellValue(x, y0 + dy, z);
                int contents = Terrain.ExtractContents(value);
                if (contents == 0 || contents == BedrockBlock.Index) continue;
                Block block = BlocksManager.Blocks[contents];
                if (!block.IsCollidable) continue;                       // [API?]
                float resilience = block.GetDigResilience(value);       // [API?]
                if (resilience <= 0f || float.IsInfinity(resilience)) continue;

                Point3 cell = new Point3(x, y0 + dy, z);
                if (cell != m_breakCell) { m_breakCell = cell; m_breakProgress = 0f; }

                float needed = MathUtils.Clamp(resilience * BreakSecondsPerResilience, BreakMinSeconds, BreakMaxSeconds);
                m_breakProgress += dt;
                if (m_breakProgress >= needed)
                {
                    m_subsystemTerrain.DestroyCell(0, cell.X, cell.Y, cell.Z, 0, false, false);   // [API?]
                    ResetBreak();
                    m_stuckTime = 0f;
                }
                return;
            }
        }

        // Interactua con la celda como lo haria el jugador (abre/cierra la puerta).
        private bool TryInteract(int x, int y, int z, int value, int contents)
        {
            Vector3 eye = m_componentCreature.ComponentCreatureModel.EyePosition;
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            Ray3 ray = new Ray3(eye, Vector3.Normalize(center - eye));

            TerrainRaycastResult? hit = m_componentMiner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Interaction);   // [API?]
            if (!hit.HasValue) return false;

            SubsystemBlockBehavior[] behaviors = m_subsystemBlockBehaviors.GetBlockBehaviors(contents);                 // [API?]
            for (int i = 0; i < behaviors.Length; i++)
            {
                if (behaviors[i].OnInteract(hit.Value, m_componentMiner)) return true;
            }
            return false;
        }
    }
}
