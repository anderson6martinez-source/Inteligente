using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;

namespace Game
{
    // Spawn natural del Indigena inteligente en TODOS los biomas.
    // No mira temperatura ni humedad: solo pide suelo firme (pasto, tierra, arena, nieve, grava)
    // y 2 bloques de aire encima. Sale solo o en grupitos de 1-2.
    //
    // SIN COMPILAR: el patron (RegisterHook "InitializeCreatureTypes" + CreatureType) sale de lo que
    // usan tus otros loaders. Lo dudoso esta marcado con [API?]; si el compilador marca algo, pasame el error.
    public class IndigenaInteligenteSpawnLoader : ModLoader
    {
        private const string TemplateName = "IndigenaInteligente";
        private const float SpawnWeight = 0.3f;     // mas alto = aparece mas seguido
        private const int GroupMin = 1;
        private const int GroupMax = 2;

        private static readonly Engine.Random s_random = new Engine.Random();

        // Bloques de suelo validos (por nombre de clase). Si no existe alguno, simplemente no cuenta.
        private static readonly string[] GroundNames =
            { "GrassBlock", "DirtBlock", "SandBlock", "SnowBlock", "GravelBlock", "SandstoneBlock", "ClayBlock" };

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("InitializeCreatureTypes", this);
        }

        public override void InitializeCreatureTypes(SubsystemCreatureSpawn subsystemCreatureSpawn,
                                                     List<SubsystemCreatureSpawn.CreatureType> creatureTypes)   // [API?]
        {
            creatureTypes.Add(new SubsystemCreatureSpawn.CreatureType(
                TemplateName, SpawnLocationType.Surface, true, false)
            {
                // [API?] los nombres de estas dos propiedades y su firma
                SpawnSuitabilityFunction = delegate (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point)
                {
                    return IsGoodSpot(subsystemCreatureSpawn, point) ? SpawnWeight : 0f;
                },
                SpawnFunction = delegate (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point)
                {
                    return subsystemCreatureSpawn.SpawnCreatures(
                        creatureType, TemplateName, point, s_random.Int(GroupMin, GroupMax));
                }
            });
        }

        private static bool IsGoodSpot(SubsystemCreatureSpawn subsystemCreatureSpawn, Point3 point)
        {
            SubsystemTerrain terrain = subsystemCreatureSpawn.Project.FindSubsystem<SubsystemTerrain>(true);
            int below = Terrain.ExtractContents(terrain.Terrain.GetCellValue(point.X, point.Y - 1, point.Z));
            if (below == 0) return false;

            string name = BlocksManager.Blocks[below].GetType().Name;
            bool solidGround = false;
            for (int i = 0; i < GroundNames.Length; i++)
            {
                if (GroundNames[i] == name) { solidGround = true; break; }
            }
            if (!solidGround) return false;

            // espacio libre para que quepa de pie
            return Terrain.ExtractContents(terrain.Terrain.GetCellValue(point.X, point.Y, point.Z)) == 0
                && Terrain.ExtractContents(terrain.Terrain.GetCellValue(point.X, point.Y + 1, point.Z)) == 0;
        }
    }
}
