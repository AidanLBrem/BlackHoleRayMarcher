#define FLAG_NONE                   0u
#define FLAG_NEEDS_LINEAR_MARCH     (1u << 0)  // trace a straight ray
#define FLAG_NEEDS_GEODESIC_MARCH   (1u << 1)  // trace a curved ray near black hole
#define FLAG_NEEDS_REFLECTION       (1u << 2)  // hit something, do BRDF
#define FLAG_NEEDS_SKYBOX           (1u << 3)  // missed everything, sample sky
#define FLAG_DONE                   (1u << 4)  // ray is finished
#define FLAG_NEEDS_CLASSIFY         (1u << 5)  // needs SOI reclassification
bool HasFlag(uint flags, uint flag)   { return (flags & flag) != 0u; }
uint SetFlag(uint flags, uint flag)   { return flags | flag; }
uint ClearFlag(uint flags, uint flag) { return flags & ~flag; }