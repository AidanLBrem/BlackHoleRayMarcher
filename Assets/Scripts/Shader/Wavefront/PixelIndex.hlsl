int getPixelIndex(v2f i)
{
    uint2 numPixels = _ScreenParams.xy;
    uint2 pixelCoord = i.uv * numPixels;
    uint pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;
    return pixelIndex;
}