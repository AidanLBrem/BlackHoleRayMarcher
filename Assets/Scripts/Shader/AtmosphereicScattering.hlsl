float atmosphereRadius;
float densityFalloffRayleigh;
float densityAtPoint(float3 samplePoint)
{
    float heightAboveSurface = samplePoint.y;
    float height1 = heightAboveSurface / (atmosphereRadius);
    height1 = clamp(height1, 0.0, 1.0);
    float localDensity = exp(-height1 * densityFalloffRayleigh) * (1.0 - height1);
    
    return localDensity;
    
}