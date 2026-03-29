#ifndef ATMOSPHEREIC_SCATTERING_INCLUDED
#define ATMOSPHEREIC_SCATTERING_INCLUDED

//Atmosphereic scattering code, assuming planet surface is at 0,0

float atmosphereRadius; //radius of atmosphere SPHERE, so it'll be surface + atmosphereHeight
float planetRadius; //radius of planet, NOT counting atmospehre
float densityFalloffRayleigh; //how fast rayleigh falls off
int numOpticalDepthPoints; //How many optical depth points are used for scattering. 8 is OK, 16 for high accuracy 
float3 rayleighScatteringCoefficients; 
float3 mieScatteringCoefficients;
uniform float mieForwardScatter;
uniform float mieBackwardScatter;
uniform float densityFalloffMie; //how fast mie falls off. Should be a decent bit higher than rayleigh
float3 sunLightColor;
uniform int applyRayleigh;
uniform int applyMie;
uniform int applySunDisk;

static const float ATM_EPS = 5;
static const float ATM_BIG = 3.402823e+38;

//TODO: Might be faster just to set atmosphereRadius = to this
float AtmosphereThickness()
{
    return max(atmosphereRadius - planetRadius, 1.0);
}
//0,0 is top of surface, so planet center must be below that
float3 PlanetCenter()
{
    return float3(0.0, -planetRadius, 0.0);
}
//how high are we on the 3D sphere of the planet
float AltitudeAboveSurface(float3 worldPos)
{
    float3 center = PlanetCenter();
    return length(worldPos - center) - planetRadius;
}
//Returns a value from 0 to 1 saying what % of atmosphereic height we are in
float Height01(float3 worldPos)
{
    return saturate(AltitudeAboveSurface(worldPos) / AtmosphereThickness());
}

bool IsInsideAtmosphere(float3 worldPos)
{
    float3 center = PlanetCenter();
    return length(worldPos - center) < atmosphereRadius;
}

bool IsInsidePlanet(float3 worldPos)
{
    float3 center = PlanetCenter();
    return length(worldPos - center) < planetRadius;
}

float RaySphereNearestForwardHit(
    float3 rayOrigin,
    float3 rayDirection,
    float3 sphereCenter,
    float sphereRadius)
{
    float3 oc = rayOrigin - sphereCenter;
    float b = dot(oc, rayDirection);
    float c = dot(oc, oc) - sphereRadius * sphereRadius;
    float h = b * b - c;

    if (h < 0.0)
        return -1.0;

    h = sqrt(h);
    float t0 = -b - h;
    float t1 = -b + h;

    if (t0 > ATM_EPS) return t0;
    if (t1 > ATM_EPS) return t1;
    return -1.0;
}


// Distance to atmosphere exit only. Used for sun rays
float AtmosphereExitDistance(float3 rayOrigin, float3 rayDirection)
{
    float3 center = PlanetCenter();
    float tAtm = RaySphereExitDistance(rayOrigin, rayDirection, center, atmosphereRadius);
    return (tAtm > ATM_EPS) ? tAtm : -1.0;
}


// True if the sun ray from this point hits the planet before exiting atmosphere.
bool SunRayHitsGround(float3 point1, float3 sunDir, float maxDistance)
{
    float3 center = PlanetCenter();
    float tGround = RaySphereNearestForwardHit(point1, sunDir, center, planetRadius);
    return (tGround > ATM_EPS && tGround < maxDistance);
}

//Density functions, exact same except which falloff value we use
float densityAtPointRayleigh(float3 samplePoint)
{
    float h = Height01(samplePoint);
    return exp(-h * densityFalloffRayleigh);
}

float densityAtPointMie(float3 samplePoint)
{
    float h = Height01(samplePoint);
    return exp(-h * densityFalloffMie);
}

//Just get a random point on the ray
float3 randomPointOnRay(float3 rayOrigin, float3 rayDirection, float maxDistance, inout uint rngState)
{
    float t = randomValue(rngState) * maxDistance;
    return rayOrigin + rayDirection * t;
}

//tauR and tauM are density values for rayleigh and mie. 
//Split the ray into chunks, query a point on that chunk, and see what the rayleigh and mie density is. Accumulate and return
void opticalDepthRM(
    float3 rayOrigin,
    float3 rayDirection,
    float rayLength,
    out float tauR,
    out float tauM)
{
    tauR = 0.0;
    tauM = 0.0;

    if (rayLength <= 0.0)
        return;

    int sampleCount = max(numOpticalDepthPoints, 2);
    float stepSize = rayLength / (sampleCount - 1);

    [loop]
    for (int i = 0; i < sampleCount; i++)
    {
        float t = stepSize * i;
        float3 pt = rayOrigin + rayDirection * t;

        tauR += densityAtPointRayleigh(pt) * stepSize;
        tauM += densityAtPointMie(pt) * stepSize;
    }
}
float3 transmittanceFromOpticalDepth(float tauR, float tauM)
{
    return exp(-(rayleighScatteringCoefficients * tauR +
                 mieScatteringCoefficients      * tauM));
}
//phase functions
float phaseRayleighFunc(float cosTheta)
{
    float cosTheta2 = cosTheta * cosTheta;
    return 3.0 / (16.0 * PI) * (1.0 + cosTheta2);
}

float phaseMieFunc(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-6);
    return (3.0 / (8.0 * PI)) * ((1.0 - g2) * (1.0 + cosTheta * cosTheta)) / pow(denom, 1.5);
}


float3 calculateLight(
    float3 rayOrigin,
    float3 rayDirection,
    float rayLength,
    inout uint rngState,
    int inScatteringPoints)
{   //should never happen
    if (rayLength <= 0.0)
        return float3(0.0, 0.0, 0.0);

    float3 result = float3(0.0, 0.0, 0.0);
    //sunDirection should be normalized
    float3 dirToSun = sunDirection;


    float3 accumR = 0.0;
    float3 accumM = 0.0;

    int sampleCount = max(inScatteringPoints, 1);
    float ds = rayLength / sampleCount;

    HitInfo firstSunDiskOccluder = (HitInfo)0;

    [loop]
    for (int i = 0; i < sampleCount; i++)
    {
        //sample at midpoint along each segment of ray
        float t = ((float)i + 0.5) * ds;
        float3 samplePoint = rayOrigin + rayDirection * t;
        //if any point is in the planet, we are occluded and break
        if (IsInsidePlanet(samplePoint))
            return float3(0,0,0);

        //optical depth from camera to this sample.
        float tauViewR, tauViewM;
        opticalDepthRM(rayOrigin, rayDirection, t, tauViewR, tauViewM);

        //sun rays uses exit distance only - the ray FROM the sun to the scattering point
        //TODO: When black hole stuff is added, we need to replace this with the distance the ray CURVED ray travels before leaving the atmosphere
        float sunSegment = AtmosphereExitDistance(samplePoint, dirToSun);
        if (sunSegment <= ATM_EPS)
            continue;

        //Check to see if the sun ray hits the ground before escaping, if it does, no scattering
        if (SunRayHitsGround(samplePoint, dirToSun, sunSegment))
            continue;

        //Scene-object occlusion, check to see if a sun ray can scatter into us
        Ray shadowRay;
        shadowRay.position  = samplePoint;
        shadowRay.direction = dirToSun;
        //TODO: probably want to replace this with a cheaper, return if we hit ANYTHING check
        HitInfo h = queryCollisions(shadowRay, sunSegment, true);
        if (i == 0)
        {
            firstSunDiskOccluder = h; 
        }
        //if we hit something, break. No light from the sun can reach this point
        if (h.didHit)
            continue;

        float tauSunR, tauSunM;
        //get optical depth from the sample point to the sun
        opticalDepthRM(samplePoint, dirToSun, sunSegment, tauSunR, tauSunM);

        float3 Tview = transmittanceFromOpticalDepth(tauViewR, tauViewM);
        float3 Tsun  = transmittanceFromOpticalDepth(tauSunR, tauSunM);
        float3 T = Tview * Tsun;
        //get density at point for rayleigh and mie and attenuate
        //TODO: replace these with a LUT
        #ifdef APPLY_RAYLEIGH
        {
            float dR = densityAtPointRayleigh(samplePoint);
            accumR += dR * rayleighScatteringCoefficients * T * sunLightColor * ds;
        }
        #endif

        #ifdef APPLY_MIE
        {
            float dM = densityAtPointMie(samplePoint);
            accumM += dM * mieScatteringCoefficients * T * sunLightColor * ds;
        }
        #endif
    }
    float cosTheta = dot(rayDirection, dirToSun);
    //set up phase multipliers
    float phaseRayleigh = phaseRayleighFunc(cosTheta);
    float phaseMieFwd   = phaseMieFunc(cosTheta, mieForwardScatter);
    float phaseMieBack  = phaseMieFunc(dot(rayDirection, -dirToSun), mieBackwardScatter);
    //accumulate
    #ifdef APPLY_RAYLEIGH
    result += accumR * phaseRayleigh;
    #endif

    #ifdef APPLY_MIE
    result += accumM * (phaseMieFwd + phaseMieBack);
    #endif
    //see if we hit the sun disk, using the FIRST occlusion check
    #ifdef APPLY_SUNDISK
    {
        float sunSegment = AtmosphereExitDistance(rayOrigin, dirToSun);
        bool sunVisible =
            (sunSegment > ATM_EPS) &&
            !SunRayHitsGround(rayOrigin, dirToSun, sunSegment) &&
            !firstSunDiskOccluder.didHit;
        //if visible, do ANOTHER optical depth check
        if (sunVisible)
        {
            float tauSunR, tauSunM;
            opticalDepthRM(rayOrigin, dirToSun, sunSegment, tauSunR, tauSunM);
            float3 Tsun = transmittanceFromOpticalDepth(tauSunR, tauSunM);

            float sunDisk = pow(max(dot(rayDirection, dirToSun), 0.0), 5096.0);
            result += sunDisk * Tsun * sunLightColor;
        }
    }
    #endif

    return result;
}
//Direct sunlighting functions
float3 evaluateDirectSunAtHit(
    float3 hitPoint,
    float3 N,
    float3 Ng,
    float3 V,
    float3 baseColor,
    float metallic,
    float roughness,
    float3 F0)
{
    //GGX stuff, see HandleReflection for what's going on
    float3 L = (sunDirection);

    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V));

    if (NdotL <= 0.0 || NdotV <= 0.0)
        return float3(0.0, 0.0, 0.0);

    if (dot(L, Ng) <= 0.0)
        return float3(0.0, 0.0, 0.0);

    float3 shadowOrigin = hitPoint + Ng * 1e-4;
    
    float sunSegment = AtmosphereExitDistance(shadowOrigin, L);
    if (sunSegment <= ATM_EPS)
        return float3(0.0, 0.0, 0.0);

    //Ground occlusion by planet
    if (SunRayHitsGround(shadowOrigin, L, sunSegment))
        return float3(0.0, 0.0, 0.0);

    //Scene occlusion
    Ray shadowRay;
    shadowRay.position  = shadowOrigin;
    shadowRay.direction = L;

    HitInfo shadowHit = queryCollisions(shadowRay, sunSegment, true);
    if (shadowHit.didHit)
        return float3(0.0, 0.0, 0.0);

    float tauSunR, tauSunM;
    
    //No occlusion, get optical depth for attenuation
    opticalDepthRM(shadowOrigin, L, sunSegment, tauSunR, tauSunM);
    float3 Tsun = transmittanceFromOpticalDepth(tauSunR, tauSunM);

    float3 sunRadianceAtHit = sunLightColor * Tsun;
    //more ggx stuff
    float3 H    = safeNormalize(V + L);
    float NdotH = saturate(dot(N, H));
    float VdotH = saturate(dot(V, H));

    float3 F = FresnelSchlick(VdotH, F0);
    float  D = D_GGX(NdotH, roughness);
    float  G = G_SmithGGX(NdotV, NdotL, roughness);

    float3 specBRDF    = (F * D * G) / max(4.0 * NdotV * NdotL, 1e-8);
    float3 kd          = (1.0 - F) * (1.0 - metallic);
    float3 diffuseBRDF = kd * baseColor / PI;

    return (diffuseBRDF + specBRDF) * NdotL * sunRadianceAtHit;
}

#endif // ATMOSPHEREIC_SCATTERING_INCLUDED