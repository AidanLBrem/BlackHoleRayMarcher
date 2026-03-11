
float3 starColor(float seed)
{
    if (seed > 0.9) return float3(1.0, 0.4, 0.2);   // red
    if (seed > 0.8) return float3(1.0, 0.6, 0.3);   // orange
    if (seed > 0.7) return float3(1.0, 0.9, 0.7);   // yellow
    if (seed > 0.6) return float3(0.9, 0.95, 1.0);  // white
    if (seed > 0.5) return float3(0.7, 0.8, 1.0);   // blue-white
    if (seed > 0.4) return float3(0.5, 0.7, 1.0);   // blue

    // slight white variation
    float r = 0.9 + 0.2 * hash(seed * 13.1);
    float g = 0.9 + 0.2 * hash(seed * 37.2);
    float b = 0.9 + 0.2 * hash(seed * 71.7);

    return float3(r,g,b);
}

float starLayer(float3 dir, float scale, float threshold, float brightness)
{
    float h = hash(floor(dir * scale));
    return smoothstep(threshold, 1.0, h) * brightness;
}

float3 getStarField(float3 rayDirection)
    {
        float3 dir = normalize(rayDirection);

        // --- layer parameters (easy to tweak) ---
        float3 scales      = float3(100.0, 200.0, 500.0);
        float3 thresholds  = float3(0.999, 0.997, 0.995);
        float3 brightness  = float3(1.0, 0.8, 0.6);

        // --- star density layers ---
        float stars = 0.0;

        stars += starLayer(dir, scales.x, thresholds.x, brightness.x);
        stars += starLayer(dir, scales.y, thresholds.y, brightness.y);
        stars += starLayer(dir, scales.z, thresholds.z, brightness.z);

        if (stars <= 0.0)
            return (0,0,0);

        // --- star color ---
        float seed = hash(dir * 123.456);
        float3 color = starColor(seed);

        return color * stars;
    }