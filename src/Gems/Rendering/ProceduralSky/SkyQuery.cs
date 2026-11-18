using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.DeferredRendering;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;
using DeferredRenderer = Magic.DeferredRendering.Renderer;

namespace Magic.ProceduralSky;

/// <summary>
/// <c>query sky</c>: the world's directional lights, the one that lights it, and each factor of the sky's light, as
/// JSON.
/// </summary>
/// <remarks>
/// A tool that lights the same scene reads this instead of working the values out again. The values are the live
/// world's, in Magic's own units: metres, lux and candela, left-handed with +Y up. The lights are as the world holds
/// them at the call, so a tool that wants another hour sets the day cycle or the lights' rotation through the console
/// first, and then calls this. The illuminance is the light above the air; what the air leaves of it at a point is in
/// the sky's transmittance table on the GPU, which this does not read.
/// </remarks>
internal static class SkyQuery
{
    public static JsonObject Describe(IEcs ecs, DeferredRenderingSettings settings)
    {
        EcsSnapshot<Transform, DirectionalLight> lights = new();
        lights.Fill(ecs);
        EcsSnapshot<Sky> skies = new();
        skies.Fill(ecs);
        (Vector3 Direction, Vector3 Illuminance, DirectionalLightMode Mode)? lit = DeferredRenderer.GetSun(
            lights.First,
            lights.Second
        );

        return new()
        {
            ["lights"] = DescribeLights(lights),
            ["lit"] = DescribeLit(lit),
            ["sky"] = DescribeSky(lit, skies),
        };

        // Each directional light as the world holds it now: where it points, and the light it sends before the air
        // takes its part.
        static JsonArray DescribeLights(EcsSnapshot<Transform, DirectionalLight> lights)
        {
            JsonArray described = [];
            for (int i = 0; i < lights.Count; i++)
            {
                DirectionalLight light = lights.Second[i];
                described.Add(
                    new JsonObject
                    {
                        ["entity"] = lights.Entities[i].Id,
                        ["mode"] = light.Mode.ToString(),
                        ["rotation"] = Write(lights.First[i].Rotation),
                        ["towards"] = Write(DeferredRenderer.GetDirection(lights.First[i].Rotation)),
                        ["color"] = Write(light.Color),
                        ["intensity"] = light.Intensity,
                        ["illuminance"] = Write(DeferredRenderer.GetIlluminance(light)),
                    }
                );
            }

            return described;
        }

        // The light that the frame is lit by, by the renderer's own rule.
        static JsonNode DescribeLit((Vector3 Direction, Vector3 Illuminance, DirectionalLightMode Mode)? sun)
        {
            if (sun is not { } lit)
                return new JsonObject
                {
                    ["mode"] = "None",
                    ["towards"] = Write(DeferredRenderer.NoSun.Direction),
                    ["illuminance"] = Write(DeferredRenderer.NoSun.Illuminance),
                    ["rule"] = "no directional light: the frame is lit by a white sun of 1 lux from above",
                };

            return new JsonObject
            {
                ["mode"] = lit.Mode.ToString(),
                ["towards"] = Write(lit.Direction),
                ["illuminance"] = Write(lit.Illuminance),
                ["rule"] =
                    $"the sun while the direction to it is over {DeferredRenderer.TwilightEnd} in Y, else the moon while it is up, else the first light",
            };
        }

        // Each factor of the sky's light, by its name: the one that scales the sky a surface sees, and the ones that
        // scale the open sky, which the air and the clouds take and which lights a world where no entity draws a sky.
        JsonObject DescribeSky(
            (Vector3 Direction, Vector3 Illuminance, DirectionalLightMode Mode)? sun,
            EcsSnapshot<Sky> skies
        )
        {
            // The sky's tables take the first sky entity's brightness, so a domain holds one sky.
            float brightness = skies.Count > 0 ? skies.First[0].Brightness : 1;
            float litRed = (sun?.Illuminance ?? DeferredRenderer.NoSun.Illuminance).X;

            return new()
            {
                ["hasSky"] = skies.Count > 0,
                ["brightness"] = brightness,
                ["openSky"] = new JsonObject
                {
                    ["setting"] = settings.SkyBrightness,
                    ["tint"] = Write(DeferredRenderer.GetSkyRadiance(1)),
                    ["litIlluminanceRed"] = litRed,
                    ["radiance"] = Write(DeferredRenderer.GetSkyRadiance(settings.SkyBrightness) * litRed * brightness),
                },
            };
        }

        static JsonNode? Write<T>(T value)
        {
            Result<JsonElement> written = Json.Write(value, typeof(T));

            return written.Ok ? JsonNode.Parse(written.Value.GetRawText()) : null;
        }
    }
}
