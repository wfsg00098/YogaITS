// Offscreen regression of the ACTUAL glass HLSL, with synthetic texture atlases.
// No HWND, capture, desktop pixels, runtime configuration or application launch.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>
using Microsoft::WRL::ComPtr;
constexpr UINT Side = 128;
using Pixel = std::array<float, 4>;
using Constants = std::array<float, 32>;
struct Layout { UINT width, height, x, y; UINT outputWidth = Side, outputHeight = Side; };
void Check(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("D3D failure: " + std::to_string(hr)); }
ComPtr<ID3DBlob> Compile(const std::string& code, const char* entry, const char* profile)
{
    ComPtr<ID3DBlob> blob, error;
    HRESULT hr = D3DCompile(code.data(), code.size(), "GlassAtlasCheck", nullptr, nullptr, entry, profile,
        D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &blob, &error);
    if (error) std::cerr.write(static_cast<const char*>(error->GetBufferPointer()), error->GetBufferSize());
    Check(hr); return blob;
}
int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc < 2 || argc > 3) throw std::runtime_error("Pass the path to Effects/Shaders/LiquidGlass.hlsl.");
        std::ifstream input(std::filesystem::path(argv[1]), std::ios::binary);
        if (!input) throw std::runtime_error("Cannot read material HLSL.");
        std::string shader{std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
        if (argc == 3 && std::wstring(argv[2]) != L"--library")
            throw std::runtime_error("Unknown shader-check mode.");
        auto library = Compile(shader, nullptr, "lib_4_0_level_9_3_ps_only");
        std::cout << "PASS: native DWM shader library; bytes=" << library->GetBufferSize() << '\n';
        if (argc == 3) return 0;
        // Remove only the library export annotation; retain all material code.
        auto psCode = Compile("#define export\n" + shader + R"(
cbuffer TestGeometry : register(b1) { float4 ContentRect; float4 AtlasSize; };
float4 TestPixel(float4 p : SV_Position) : SV_Target
{
    float2 uv = ContentRect.xy + p.xy / AtlasSize.zw * (ContentRect.zw - ContentRect.xy);
    return LiquidGlassBody(uv, AtlasSize, ContentRect);
})", "TestPixel", "ps_5_0");
        auto vsCode = Compile(R"(
float4 TestVertex(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
})", "TestVertex", "vs_5_0");
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<ID3D11PixelShader> ps; ComPtr<ID3D11VertexShader> vs;
        Check(device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &ps));
        Check(device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &vs));
        context->VSSetShader(vs.Get(), nullptr, 0); context->PSSetShader(ps.Get(), nullptr, 0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11_VIEWPORT viewport{0, 0, float(Side), float(Side), 0, 1};
        context->RSSetViewports(1, &viewport);
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler; Check(device->CreateSamplerState(&sd, &sampler));
        auto samplerPtr = sampler.Get(); context->PSSetSamplers(0, 1, &samplerPtr);
        D3D11_TEXTURE2D_DESC td{}; td.Width = td.Height = 512;
        td.MipLevels = td.ArraySize = td.SampleDesc.Count = 1;
        td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT; td.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> target, readback; Check(device->CreateTexture2D(&td, nullptr, &target));
        ComPtr<ID3D11RenderTargetView> rtv; Check(device->CreateRenderTargetView(target.Get(), nullptr, &rtv));
        auto rtvPtr = rtv.Get(); context->OMSetRenderTargets(1, &rtvPtr, nullptr);
        td.BindFlags = 0; td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Check(device->CreateTexture2D(&td, nullptr, &readback));
        auto render = [&](Layout layout, const Constants& material, float trackInset = -1.0f, bool hairline = false)
        {
            std::vector<Pixel> pixels(size_t(layout.width) * layout.height);
            for (UINT y = 0; y < layout.height; ++y)
                for (UINT x = 0; x < layout.width; ++x)
                {
                    // Edge-extend the same image into padding for identical filtering.
                    int px = std::clamp(int(x) - int(layout.x), 0, int(layout.outputWidth) - 1);
                    int py = std::clamp(int(y) - int(layout.y), 0, int(layout.outputHeight) - 1);
                    pixels[size_t(y) * layout.width + x] = {float(px) / (Side - 1), float(py) / (Side - 1),
                        ((px / 8 + py / 8) % 2) ? 0.8f : 0.1f, 1};
                    if (trackInset >= 0)
                    {
                        float depth = std::min(float(py) + 0.5f, float(layout.outputHeight - py) - 0.5f) / material[31];
                        float distance = std::abs(depth - trackInset) * (hairline ? material[31] : 1.0f);
                        float line = std::clamp(1.0f - distance, 0.0f, 1.0f);
                        pixels[size_t(y) * layout.width + x] = {line, line, line, 1};
                    }
                }
            D3D11_TEXTURE2D_DESC desc{}; desc.Width = layout.width; desc.Height = layout.height;
            desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
            desc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA data{pixels.data(), UINT(layout.width * sizeof(Pixel)), 0};
            ComPtr<ID3D11Texture2D> texture; Check(device->CreateTexture2D(&desc, &data, &texture));
            ComPtr<ID3D11ShaderResourceView> srv; Check(device->CreateShaderResourceView(texture.Get(), nullptr, &srv));
            auto srvPtr = srv.Get(); context->PSSetShaderResources(0, 1, &srvPtr);
            std::array<float, 8> geometry{float(layout.x) / layout.width, float(layout.y) / layout.height,
                float(layout.x + layout.outputWidth) / layout.width, float(layout.y + layout.outputHeight) / layout.height,
                float(layout.width), float(layout.height), float(layout.outputWidth), float(layout.outputHeight)};
            auto buffer = [&](const void* values, UINT bytes)
            {
                D3D11_BUFFER_DESC bd{}; bd.ByteWidth = bytes; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                D3D11_SUBRESOURCE_DATA init{values, 0, 0}; ComPtr<ID3D11Buffer> result;
                Check(device->CreateBuffer(&bd, &init, &result)); return result;
            };
            auto constants = buffer(material.data(), UINT(sizeof(material)));
            auto coords = buffer(geometry.data(), UINT(sizeof(geometry)));
            ID3D11Buffer* buffers[]{constants.Get(), coords.Get()}; context->PSSetConstantBuffers(0, 2, buffers);
            D3D11_VIEWPORT outputViewport{0, 0, float(layout.outputWidth), float(layout.outputHeight), 0, 1};
            context->RSSetViewports(1, &outputViewport);
            context->Draw(3, 0); context->CopyResource(readback.Get(), target.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{}; Check(context->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
            std::vector<Pixel> output(size_t(layout.outputWidth) * layout.outputHeight);
            for (UINT y = 0; y < layout.outputHeight; ++y)
            {
                auto row = reinterpret_cast<const Pixel*>(static_cast<const char*>(mapped.pData) + y * mapped.RowPitch);
                std::copy_n(row, layout.outputWidth, output.data() + size_t(y) * layout.outputWidth);
            }
            context->Unmap(readback.Get(), 0); return output;
        };
        int comparisons = 0, failures = 0;
        for (float scale : {1.0f, 2.0f}) for (float dispersion : {0.0f, 7.0f}) for (float zoom : {0.90f, 1.0f, 1.25f})
        {
            Constants material{20, 1.4f, dispersion, 15, 20, 20, 15, 20, 90, 50, 80, -45,
                0, 1, 255, 255, 255, 0.4f, zoom, 0, 0, 0, 0, 0, 0.125f, 2, 0, 0, 0, 1, 0, scale};
            auto baseline = render({Side, Side, 0, 0}, material);
            for (Layout atlas : {Layout{256,256,32,64}, Layout{512,256,128,32}, Layout{1024,1024,256,384}})
            {
                auto image = render(atlas, material); float worst = 0;
                for (size_t i = 0; i < image.size(); ++i) for (size_t c = 0; c < 4; ++c)
                {
                    if (!std::isfinite(image[i][c])) throw std::runtime_error("Non-finite shader output.");
                    worst = std::max(worst, std::abs(image[i][c] - baseline[i][c]));
                }
                std::cout << "atlas=" << atlas.width << 'x' << atlas.height << " scale=" << scale
                    << " dispersion=" << dispersion << " zoom=" << zoom << " max_error=" << worst << '\n';
                // Less than half a quantization step in the actual BGRA8 composition target.
                if (worst > 0.5f / 255) ++failures;
                ++comparisons;
            }
        }
        // A pressed 48-DIP capsule grows to 72 DIPs around a 60-DIP track.
        // Its track outline is six DIPs INSIDE the lens, and must remain visible
        // through refraction toward the image center, not a duplicate overlay.
        int trackCases = 0, seamCases = 0;
        double minGap = 1000, maxGap = 0;
        for (float scale : {1.0f, 1.25f, 1.5f, 2.0f, 3.0f})
        for (float dispersion : {0.0f, 7.0f})
        {
            UINT width = UINT(160 * scale), height = UINT(72 * scale);
            Constants material{25, 1.4f, dispersion, 15, 20, 0, 15, 20, 0, 50, 0, -45,
                0, 1, 255, 255, 255, 0, 1, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 1, 0, scale};
            auto image = render({width,height,0,0,width,height}, material, 6);
            auto separation = [&](const std::vector<Pixel>& pixels, UINT column)
            {
                double total = 0, moment = 0;
                for (UINT y=0; y<UINT(24*scale); ++y)
                {
                    float intensity = pixels[size_t(y)*width+column][1];
                    total += intensity;
                    moment += intensity * (float(y)+0.5f);
                }
                if (total < 0.1) throw std::runtime_error("Nested track outline disappeared under magnification.");
                return (double(height) - 2*moment/total)/scale;
            };
            double refractedGap = separation(image,width/2);
            minGap = std::min(minGap,refractedGap);
            maxGap = std::max(maxGap,refractedGap);
            Constants identity = material;
            identity[1] = identity[18] = 1; // No refraction and no background zoom.
            auto flat = render({width,height,0,0,width,height}, identity, 6);
            double flatGap = separation(flat,width/2);
            UINT curvedColumn = width-UINT(20*scale);
            double curvedGap = separation(image,curvedColumn);
            std::cout << "image contraction baseline=" << flatGap << " refracted=" << refractedGap
                << " curved_end=" << curvedGap << " DIP\n";
            if (refractedGap >= flatGap-1)
                throw std::runtime_error("Capsule refraction must move track edges toward the image center, without zoom.");
            if (std::abs(curvedGap-refractedGap) < 0.5)
                throw std::runtime_error("Capsule curved ends must bend a straight underlying track edge.");
            for (bool bottom : {false,true})
            {
                float peak = 0; UINT peakY = 0;
                for (UINT y=0; y<UINT(24*scale); ++y)
                {
                    UINT row = bottom ? height-1-y : y;
                    if (image[size_t(row)*width+width/2][1] > peak)
                    { peak=image[size_t(row)*width+width/2][1]; peakY=y; }
                }
                std::cout << "track scale=" << scale << " dispersion=" << dispersion
                    << " bottom=" << bottom << " refracted_peak=" << peak << " depth=" << peakY << '\n';
                if (peak < 0.1f) throw std::runtime_error("Enlarged capsule skipped the nested track outline.");
                ++trackCases;
            }
            // Composite over the original unwarped track, exactly as a
            // translucent lens sits above a longer visible track in the app.
            // Follow its actual bright pixels across the curved lens boundary;
            // checking only the center width cannot detect a broken join.
            auto connected = [&](const std::vector<Pixel>& lens, bool bottom, size_t channel, bool hairline)
            {
                UINT bandHeight = UINT(24*scale), start = width/2;
                std::vector<unsigned char> visited(size_t(width)*bandHeight);
                std::vector<size_t> queue;
                auto push = [&](UINT x, UINT y)
                {
                    size_t index = size_t(y)*width+x;
                    if (visited[index]) return;
                    visited[index] = 1;
                    UINT row = bottom ? height-1-y : y;
                    const auto& pixel = lens[size_t(row)*width+x];
                    float original = std::clamp(1.0f-std::abs((float(y)+0.5f)/scale-6.0f)*(hairline ? scale : 1.0f),0.0f,1.0f);
                    float composed = pixel[channel] + (1.0f-pixel[3])*original;
                    if (composed > (hairline ? 0.05f : 0.08f)) queue.push_back(index);
                };
                for (UINT y=0;y<bandHeight;++y) push(start,y);
                for (size_t i=0;i<queue.size();++i)
                {
                    UINT x=UINT(queue[i]%width), y=UINT(queue[i]/width);
                    if (x==width-1) return true;
                    for (int dy=-1;dy<=1;++dy) for (int dx=-1;dx<=1;++dx)
                    {
                        int nx=int(x)+dx, ny=int(y)+dy;
                        if (nx>=int(start) && nx<int(width) && ny>=0 && ny<int(bandHeight))
                            push(UINT(nx),UINT(ny));
                    }
                }
                return false;
            };
            auto thin = render({width,height,0,0,width,height}, material, 6, true);
            for (bool hairline : {false,true}) for (bool bottom : {false,true})
            for (size_t channel : {size_t(0),size_t(1),size_t(2)})
            {
                if (!connected(hairline ? thin : image,bottom,channel,hairline))
                    throw std::runtime_error("Refracted track is disconnected from the outside track at the curved glass boundary.");
                ++seamCases;
            }
            auto padded = render({512,512,16,32,width,height}, material, 6);
            float atlasError = 0;
            for (size_t i=0; i<image.size(); ++i) for (size_t c=0; c<4; ++c)
            {
                if (!std::isfinite(padded[i][c])) throw std::runtime_error("Non-finite nested track output.");
                atlasError = std::max(atlasError, std::abs(padded[i][c]-image[i][c]));
            }
            std::cout << "track atlas_error=" << atlasError << '\n';
            // Fractional DPI uses non-power-of-two textures: D3D bilinear
            // interpolation has 8-bit subtexel precision on each of two axes.
            if (atlasError > 2.0f/255)
                throw std::runtime_error("Nested track refraction changed with texture atlas placement.");
        }
        std::cout << "PASS: " << seamCases << " RGB track boundary joins, including one-physical-pixel lines.\n";
        std::cout << "track logical gap drift=" << maxGap-minGap << " DIP\n";
        if (maxGap-minGap > 0.8)
            throw std::runtime_error("Logical refraction strength changed with monitor DPI.");
        std::cout << "PASS: " << trackCases << " enlarged-capsule inward-image and curved-track refraction cases.\n";
        // Encode source coordinates in R/G. A folded inverse map reverses
        // the sign of this actual rendered Jacobian and creates sharp notches
        // even when a thresholded line remains connected across the lens edge.
        double minimumJacobian = 1000;
        int warpCases = 0;
        for (float scale : {1.0f,1.25f,1.5f,2.0f,3.0f}) for (float heightDip : {48.0f,72.0f})
        {
            UINT width=UINT(160*scale), height=UINT(heightDip*scale);
            Constants material{25,1.4f,0,15,20,0,15,20,0,50,0,-45,
                0,1,255,255,255,0,1,0,0,0,0,0,1,2,0,0,0,1,0,scale};
            auto field=render({width,height,0,0,width,height},material);
            double worst=1000;
            for (UINT y=1;y+1<height;++y) for (UINT x=1;x+1<width;++x)
            {
                const Pixel& c=field[size_t(y)*width+x];
                const Pixel& l=field[size_t(y)*width+x-1];
                const Pixel& r=field[size_t(y)*width+x+1];
                const Pixel& t=field[size_t(y-1)*width+x];
                const Pixel& b=field[size_t(y+1)*width+x];
                if (std::min({c[3],l[3],r[3],t[3],b[3]})<0.9999f) continue;
                double xx=(r[0]-l[0])*(Side-1)*0.5, xy=(b[0]-t[0])*(Side-1)*0.5;
                double yx=(r[1]-l[1])*(Side-1)*0.5, yy=(b[1]-t[1])*(Side-1)*0.5;
                worst=std::min(worst,xx*yy-xy*yx);
            }
            std::cout << "warp scale=" << scale << " height=" << heightDip << " min_jacobian=" << worst << '\n';
            minimumJacobian=std::min(minimumJacobian,worst);
            ++warpCases;
        }
        if (minimumJacobian<=0.02)
            throw std::runtime_error("Glass inverse sampling folds over itself near the contour.");
        std::cout << "PASS: " << warpCases << " nonfolding curved refraction fields.\n";
        int cornerCases = 0;
        for (float scale : {1.0f, 1.25f, 1.5f, 2.0f, 3.0f, 4.0f})
        for (float neutral : {0.0f, 255.0f}) for (float amount : {0.0f, 0.4f, 1.0f})
        {
            Constants material{20, 1.0f, 0, 15, 20, 0, 15, 20, 0, 50, 0, -45,
                0, 1, neutral, neutral, neutral, amount, 1, 0, 1, 0, 0, 0,
                16.0f * scale / Side, 2, 0, 0, 0, 1, 0, scale};
            auto window = render({Side, Side, 0, 0}, material);
            for (const auto& pixel : window)
                if (!std::isfinite(pixel[3]) || std::abs(pixel[3]-1.0f) > 1e-6f)
                    throw std::runtime_error("Window tint coverage contains a transparent hole.");
            for (auto point : {std::array<UINT,2>{0,0}, {Side-1,0}, {0,Side-1}, {Side-1,Side-1}})
            {
                UINT x=point[0], y=point[1];
                Pixel expected{float(x)/(Side-1),float(y)/(Side-1),((x/8+y/8)%2)?0.8f:0.1f,1};
                for (size_t channel=0;channel<3;channel++)
                {
                    float tinted=expected[channel]*(1-amount)+neutral/255.0f*amount;
                    if (std::abs(window[size_t(y)*Side+x][channel]-tinted)>2e-5f)
                        throw std::runtime_error("A window corner exposed untinted background.");
                }
            }
            material[20]=0; // Control mode must preserve the transparent outside shape.
            auto control=render({Side,Side,0,0},material);
            for (auto point : {std::array<UINT,2>{0,0}, {Side-1,0}, {0,Side-1}, {Side-1,Side-1}})
                for (float channel : control[size_t(point[1])*Side+point[0]])
                    if (std::abs(channel)>1e-6f)
                        throw std::runtime_error("The optical contour retained tint outside its highlight.");
            cornerCases++;
        }
        std::cout << "PASS: " << cornerCases << " window/corner tint and cut-contour cases; no tint outside the optical contour.\n";
        if (failures) throw std::runtime_error("Glass changed when only texture atlas layout changed: " + std::to_string(failures));
        std::cout << "PASS: " << comparisons << " offscreen shader atlas-invariance comparisons (synthetic pixels only).\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
