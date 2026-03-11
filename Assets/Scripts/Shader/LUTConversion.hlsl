
float RadiusToLUT_U(float r, float rs, float rMinOverRs, float rMaxOverRs, float logEpsilonOverRs)
{
    float rMin = rMinOverRs * rs;
    float rMax = rMaxOverRs * rs;
    float eps  = logEpsilonOverRs * rs;

    float a = log((rMin - rs) + eps);
    float b = log((rMax - rs) + eps);

    float u = (log((r - rs) + eps) - a) / (b - a);
    return saturate(u);
}

float MuToLUT_V(float mu, int muResolution)
{
    // Inverse of (y + 0.5) / muResolution — map abs(mu) into cell-centre space
    float absMu = saturate(abs(mu));
    return (absMu * muResolution - 0.5) / muResolution;
}
            
