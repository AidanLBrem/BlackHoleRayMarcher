#define FLAG_NONE                   0u
#define FLAG_NEEDS_LINEAR_MARCH     (1u << 0)
#define FLAG_NEEDS_GEODESIC_MARCH   (1u << 1)
#define FLAG_NEEDS_NEE_LINEAR       (1u << 2)
#define FLAG_NEEDS_NEE_GEODESIC     (1u << 3)
#define FLAG_NEEDS_SCATTER_LINEAR   (1u << 4)
#define FLAG_NEEDS_SCATTER_GEODESIC (1u << 5)
#define FLAG_NEEDS_SKYBOX           (1u << 6)
#define FLAG_DONE                   (1u << 7)

bool HasFlag(uint flags, uint flag)   { return (flags & flag) != 0u; }
uint SetFlag(uint flags, uint flag)   { return flags | flag; }
uint ClearFlag(uint flags, uint flag) { return flags & ~flag; }
uint SetFlags(uint flags)             { return flags; }