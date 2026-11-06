#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <DirectXMath.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <vector>
#include <algorithm>
#include <cstring>
#include <cmath>
#include <unordered_set>
#include <microsoft.ui.xaml.media.dxinterop.h>

using Microsoft::WRL::ComPtr;
using namespace DirectX;

struct Vertex
{
    float x, y, z;
    float nx, ny, nz;
    float u, v;
    unsigned color;
};
static_assert(sizeof(Vertex) == 36);

struct Mesh
{
    ComPtr<ID3D11Buffer> vertices;
    ComPtr<ID3D11Buffer> indices;
    ComPtr<ID3D11ShaderResourceView> texture;
    unsigned indexCount = 0;
    int mode = 0;
    unsigned long long bytes = 0;
    unsigned long long textureBytes = 0;
    float center[3]{};
};

struct DecodedTexture
{
    ComPtr<ID3D11ShaderResourceView> view;
    UINT width = 0;
    UINT height = 0;
    bool hasTransparency = false;
};

struct Renderer
{
    ComPtr<ISwapChainPanelNative> panel;
    ComPtr<IVirtualSurfaceImageSourceNative> surface;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11Texture2D> colorBuffer;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11DepthStencilView> depth;
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> pixelShader;
    ComPtr<ID3D11InputLayout> layout;
    ComPtr<ID3D11Buffer> constant;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11SamplerState> filteredSampler;
    ComPtr<ID3D11BlendState> blend;
    ComPtr<ID3D11DepthStencilState> noDepthWrite;
    std::vector<Mesh> meshes;
    std::vector<Mesh> sliceMeshes;
    unsigned long long meshBytes = 0;
    int clipAxis = -1;
    float clipMinimum = 0;
    float clipMaximum = 0;
    UINT width = 1;
    UINT height = 1;
    D3D_FEATURE_LEVEL feature{};
    float eye[3] = { 8, 8, 8 };
    float look[3] = { 0, 0, 0 };

    ~Renderer()
    {
        if (panel)
            panel->SetSwapChain(nullptr);
        if (surface)
            surface->SetDevice(nullptr);
        if (context)
        {
            context->ClearState();
            context->Flush();
        }
    }

    HRESULT Targets(UINT newWidth, UINT newHeight)
    {
        if (newWidth == 0 || newHeight == 0 ||
            newWidth > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
            newHeight > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
            return E_INVALIDARG;
        context->OMSetRenderTargets(0, nullptr, nullptr);
        target.Reset();
        depth.Reset();
        colorBuffer.Reset();
        width = newWidth;
        height = newHeight;

        HRESULT hr;
        if (surface)
        {
            hr = surface->Resize((INT)width, (INT)height);
            if (FAILED(hr))
                return hr;
            D3D11_TEXTURE2D_DESC colorDesc{};
            colorDesc.Width = width;
            colorDesc.Height = height;
            colorDesc.MipLevels = 1;
            colorDesc.ArraySize = 1;
            colorDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            colorDesc.SampleDesc.Count = 1;
            colorDesc.BindFlags = D3D11_BIND_RENDER_TARGET;
            hr = device->CreateTexture2D(&colorDesc, nullptr, &colorBuffer);
        }
        else
        {
            hr = swap->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            if (FAILED(hr))
                return hr;
            hr = swap->GetBuffer(0, IID_PPV_ARGS(&colorBuffer));
        }
        if (FAILED(hr))
            return hr;
        hr = device->CreateRenderTargetView(colorBuffer.Get(), nullptr, &target);
        if (FAILED(hr))
            return hr;

        D3D11_TEXTURE2D_DESC depthDesc{};
        depthDesc.Width = width;
        depthDesc.Height = height;
        depthDesc.MipLevels = 1;
        depthDesc.ArraySize = 1;
        depthDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
        depthDesc.SampleDesc.Count = 1;
        depthDesc.BindFlags = D3D11_BIND_DEPTH_STENCIL;
        ComPtr<ID3D11Texture2D> depthTexture;
        hr = device->CreateTexture2D(&depthDesc, nullptr, &depthTexture);
        if (FAILED(hr))
            return hr;
        return device->CreateDepthStencilView(depthTexture.Get(), nullptr, &depth);
    }

    HRESULT Init(IUnknown* unknown, UINT initialWidth, UINT initialHeight)
    {
        if (unknown == nullptr)
            return E_INVALIDARG;
        HRESULT hr = unknown->QueryInterface(IID_PPV_ARGS(&surface));
        if (FAILED(hr))
            hr = unknown->QueryInterface(IID_PPV_ARGS(&panel));
        if (FAILED(hr))
            return hr;

        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            flags,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &device,
            &feature,
            &context);
        if (FAILED(hr))
            return hr;

        ComPtr<IDXGIDevice> dxgiDevice;
        hr = device.As(&dxgiDevice);
        if (FAILED(hr))
            return hr;
        if (surface)
        {
            hr = surface->SetDevice(dxgiDevice.Get());
        }
        else
        {
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            hr = dxgiDevice->GetAdapter(&adapter);
            if (FAILED(hr))
                return hr;
            hr = adapter->GetParent(IID_PPV_ARGS(&factory));
            if (FAILED(hr))
                return hr;
            DXGI_SWAP_CHAIN_DESC1 swapDesc{};
            swapDesc.Width = initialWidth;
            swapDesc.Height = initialHeight;
            swapDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            swapDesc.SampleDesc.Count = 1;
            swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            swapDesc.BufferCount = 2;
            swapDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            swapDesc.Scaling = DXGI_SCALING_STRETCH;
            swapDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
            hr = factory->CreateSwapChainForComposition(
                device.Get(), &swapDesc, nullptr, &swap);
        }
        if (FAILED(hr))
            return hr;
        hr = Targets(initialWidth, initialHeight);
        if (FAILED(hr))
            return hr;

        const char* shader = R"(
cbuffer Camera : register(b0) {
    row_major float4x4 vp;
    float4 params;
};
Texture2D tex : register(t0);
SamplerState pointSampler : register(s0);
SamplerState filteredSampler : register(s1);
struct In {
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD;
    float4 color : COLOR;
};
struct Out {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD;
    float4 color : COLOR;
    float3 world : WORLD;
};
Out VS(In input) {
    Out output;
    output.position = mul(float4(input.position, 1), vp);
    output.uv = input.uv;
    output.color = input.color;
    output.world = input.position;
    return output;
}
float4 PS(Out input) : SV_TARGET {
    if (params.y > -0.5) {
        float coordinate = params.y < 0.5 ? input.world.x :
            params.y < 1.5 ? input.world.y : input.world.z;
        clip(coordinate - params.z);
        clip(params.w - coordinate);
    }
    uint tw, th;
    tex.GetDimensions(tw, th);
    float footprint = max(length(ddx(input.uv) * float2(tw, th)), length(ddy(input.uv) * float2(tw, th)));
    float4 color = (params.x > 0.5 && params.x < 1.5 || footprint <= 1 ? tex.SampleLevel(pointSampler, input.uv, 0) : tex.Sample(filteredSampler, input.uv)) * input.color;
    if (params.x > 0.5 && params.x < 1.5)
        clip(color.a - 0.1);

    return color;
}
)";
        ComPtr<ID3DBlob> vertexBytecode;
        ComPtr<ID3DBlob> pixelBytecode;
        ComPtr<ID3DBlob> errors;
        hr = D3DCompile(
            shader, strlen(shader), nullptr, nullptr, nullptr,
            "VS", "vs_5_0", 0, 0, &vertexBytecode, &errors);
        if (FAILED(hr))
            return hr;
        hr = D3DCompile(
            shader, strlen(shader), nullptr, nullptr, nullptr,
            "PS", "ps_5_0", 0, 0, &pixelBytecode, &errors);
        if (FAILED(hr))
            return hr;
        hr = device->CreateVertexShader(
            vertexBytecode->GetBufferPointer(),
            vertexBytecode->GetBufferSize(),
            nullptr,
            &vertexShader);
        if (FAILED(hr))
            return hr;
        hr = device->CreatePixelShader(
            pixelBytecode->GetBufferPointer(),
            pixelBytecode->GetBufferSize(),
            nullptr,
            &pixelShader);
        if (FAILED(hr))
            return hr;

        D3D11_INPUT_ELEMENT_DESC elements[] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0,
                D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12,
                D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24,
                D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, 32,
                D3D11_INPUT_PER_VERTEX_DATA, 0 }
        };
        hr = device->CreateInputLayout(
            elements, ARRAYSIZE(elements),
            vertexBytecode->GetBufferPointer(),
            vertexBytecode->GetBufferSize(),
            &layout);
        if (FAILED(hr))
            return hr;

        D3D11_BUFFER_DESC constantDesc{};
        constantDesc.ByteWidth = 80;
        constantDesc.Usage = D3D11_USAGE_DEFAULT;
        constantDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = device->CreateBuffer(&constantDesc, nullptr, &constant);
        if (FAILED(hr))
            return hr;

        D3D11_RASTERIZER_DESC rasterDesc{};
        rasterDesc.FillMode = D3D11_FILL_SOLID;
        rasterDesc.CullMode = D3D11_CULL_BACK;
        rasterDesc.DepthClipEnable = TRUE;
        hr = device->CreateRasterizerState(&rasterDesc, &rasterizer);
        if (FAILED(hr))
            return hr;

        D3D11_SAMPLER_DESC samplerDesc{};
        samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
        hr = device->CreateSamplerState(&samplerDesc, &sampler);
        if (FAILED(hr))
            return hr;

        samplerDesc.Filter = D3D11_FILTER_ANISOTROPIC;
        samplerDesc.MaxAnisotropy = 8;
        hr = device->CreateSamplerState(&samplerDesc, &filteredSampler);
        if (FAILED(hr)) return hr;

        D3D11_BLEND_DESC blendDesc{};
        auto& targetBlend = blendDesc.RenderTarget[0];
        targetBlend.BlendEnable = TRUE;
        targetBlend.SrcBlend = D3D11_BLEND_SRC_ALPHA;
        targetBlend.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        targetBlend.BlendOp = D3D11_BLEND_OP_ADD;
        targetBlend.SrcBlendAlpha = D3D11_BLEND_ONE;
        targetBlend.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        targetBlend.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        targetBlend.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        hr = device->CreateBlendState(&blendDesc, &blend);
        if (FAILED(hr))
            return hr;

        D3D11_DEPTH_STENCIL_DESC depthDesc{};
        depthDesc.DepthEnable = TRUE;
        depthDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        depthDesc.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
        hr = device->CreateDepthStencilState(&depthDesc, &noDepthWrite);
        if (FAILED(hr))
            return hr;

        return panel ? panel->SetSwapChain(swap.Get()) : S_OK;
    }

    HRESULT PresentSurface()
    {
        // WinUI can sample this XAML surface for Acrylic; SwapChainPanel is
        // composed separately. Keep frame transfer on the GPU, without readback.
        ComPtr<IDXGISurface> drawingSurface;
        POINT offset{};
        RECT update{ 0, 0, (LONG)width, (LONG)height };
        HRESULT hr = surface->BeginDraw(update, &drawingSurface, &offset);
        if (FAILED(hr))
            return hr;
        {
            ComPtr<IDXGISurface2> drawingSurface2;
            ComPtr<ID3D11Texture2D> destination;
            UINT subresource = 0;
            hr = drawingSurface.As(&drawingSurface2);
            if (SUCCEEDED(hr))
                hr = drawingSurface2->GetResource(IID_PPV_ARGS(&destination), &subresource);
            if (SUCCEEDED(hr))
            {
                context->OMSetRenderTargets(0, nullptr, nullptr);
                D3D11_BOX source{ 0, 0, 0, width, height, 1 };
                context->CopySubresourceRegion(destination.Get(), subresource,
                    offset.x, offset.y, 0, colorBuffer.Get(), 0, &source);
            }
        }
        drawingSurface.Reset();
        // Every successful BeginDraw is paired, including a failed surface query.
        HRESULT endResult = surface->EndDraw();
        return FAILED(hr) ? hr : endResult;
    }

    HRESULT Draw(bool present)
    {
        if (!target || !depth || width == 0 || height == 0)
            return E_FAIL;

        float clear[] = { 0.035f, 0.045f, 0.065f, 1.0f };
        context->ClearRenderTargetView(target.Get(), clear);
        context->ClearDepthStencilView(depth.Get(), D3D11_CLEAR_DEPTH, 1, 0);
        ID3D11RenderTargetView* renderTarget = target.Get();
        context->OMSetRenderTargets(1, &renderTarget, depth.Get());

        D3D11_VIEWPORT viewport{ 0, 0, (float)width, (float)height, 0, 1 };
        context->RSSetViewports(1, &viewport);
        context->RSSetState(rasterizer.Get());

        auto eyeVector = XMVectorSet(eye[0], eye[1], eye[2], 1);
        auto lookVector = XMVectorSet(look[0], look[1], look[2], 1);
        auto view = XMMatrixLookAtLH(eyeVector, lookVector, XMVectorSet(0, 1, 0, 0));
        auto projection = XMMatrixPerspectiveFovLH(
            XM_PIDIV4, (float)width / (float)height, 0.01f, 100000.0f);
        struct Constants
        {
            XMFLOAT4X4 matrix;
            float params[4]{};
        } constants;
        XMStoreFloat4x4(&constants.matrix, view * projection);

        context->IASetInputLayout(layout.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertexShader.Get(), nullptr, 0);
        context->PSSetShader(pixelShader.Get(), nullptr, 0);
        ID3D11Buffer* constantBuffer = constant.Get();
        context->VSSetConstantBuffers(0, 1, &constantBuffer);
        context->PSSetConstantBuffers(0, 1, &constantBuffer);
        ID3D11SamplerState* samplerState = sampler.Get();
        context->PSSetSamplers(0, 1, &samplerState);
        ID3D11SamplerState* filteredState = filteredSampler.Get();
        context->PSSetSamplers(1, 1, &filteredState);

        std::vector<Mesh*> ordered;
        ordered.reserve(meshes.size() + sliceMeshes.size());
        for (auto& mesh : meshes)
            ordered.push_back(&mesh);
        for (auto& mesh : sliceMeshes)
            ordered.push_back(&mesh);
        auto distance = [&](Mesh* mesh)
        {
            float result = 0;
            for (int i = 0; i < 3; ++i)
            {
                float delta = mesh->center[i] - eye[i];
                result += delta * delta;
            }
            return result;
        };
        std::stable_sort(ordered.begin(), ordered.end(), [&](Mesh* left, Mesh* right)
        {
            if (left->mode != right->mode)
                return left->mode < right->mode;
            return left->mode == 2 ? distance(left) > distance(right) : false;
        });

        for (auto* mesh : ordered)
        {
            context->OMSetBlendState(
                mesh->mode == 2 ? blend.Get() : nullptr, nullptr, 0xffffffff);
            context->OMSetDepthStencilState(
                mesh->mode == 2 ? noDepthWrite.Get() : nullptr, 0);
            constants.params[0] = (float)mesh->mode;
            constants.params[1] = (float)clipAxis;
            constants.params[2] = clipMinimum;
            constants.params[3] = clipMaximum;
            context->UpdateSubresource(constant.Get(), 0, nullptr, &constants, 0, 0);

            ID3D11Buffer* vertexBuffer = mesh->vertices.Get();
            UINT stride = sizeof(Vertex);
            UINT offset = 0;
            context->IASetVertexBuffers(0, 1, &vertexBuffer, &stride, &offset);
            context->IASetIndexBuffer(mesh->indices.Get(), DXGI_FORMAT_R32_UINT, 0);
            ID3D11ShaderResourceView* texture = mesh->texture.Get();
            context->PSSetShaderResources(0, 1, &texture);
            context->DrawIndexed(mesh->indexCount, 0, 0);
        }
        if (!present)
            return S_OK;
        return surface ? PresentSurface() : swap->Present(1, 0);
    }
};

#define API extern "C" __declspec(dllexport)

API int __cdecl renderer_create(
    void* panel, unsigned width, unsigned height, void** result)
{
    if (result == nullptr)
        return E_INVALIDARG;
    auto renderer = new Renderer();
    HRESULT hr = renderer->Init((IUnknown*)panel, width, height);
    if (FAILED(hr))
    {
        delete renderer;
        *result = nullptr;
        return hr;
    }
    *result = renderer;
    return S_OK;
}

API void __cdecl renderer_destroy(Renderer* renderer)
{
    delete renderer;
}

API int __cdecl renderer_resize(Renderer* renderer, unsigned width, unsigned height)
{
    return renderer == nullptr ? E_INVALIDARG : renderer->Targets(width, height);
}

API int __cdecl renderer_clear(Renderer* renderer)
{
    if (renderer == nullptr)
        return E_INVALIDARG;
    renderer->context->ClearState();
    renderer->meshes.clear();
    renderer->sliceMeshes.clear();
    renderer->meshBytes = 0;
    return S_OK;
}

API int __cdecl renderer_clear_slice(Renderer* renderer)
{
    if (renderer == nullptr)
        return E_INVALIDARG;
    for (const auto& mesh : renderer->sliceMeshes)
        renderer->meshBytes -= mesh.bytes;
    renderer->sliceMeshes.clear();
    return S_OK;
}

API int __cdecl renderer_clip(
    Renderer* renderer,
    int axis,
    float minimum,
    float maximum)
{
    if (renderer == nullptr)
        return E_INVALIDARG;
    renderer->clipAxis = axis;
    renderer->clipMinimum = minimum;
    renderer->clipMaximum = maximum;
    return S_OK;
}

API int __cdecl renderer_mesh(
    Renderer* renderer,
    const Vertex* vertices,
    unsigned vertexCount,
    const unsigned* indices,
    unsigned indexCount,
    DecodedTexture* decodedTexture,
    int mode,
    int slice)
{
    if (renderer == nullptr || vertices == nullptr || indices == nullptr ||
        decodedTexture == nullptr || !decodedTexture->view || vertexCount == 0 || indexCount == 0 ||
        vertexCount > 10000000 || indexCount > 30000000 ||
        decodedTexture->width > 16384 || decodedTexture->height > 16384)
        return E_INVALIDARG;
    for (unsigned i = 0; i < indexCount; ++i)
        if (indices[i] >= vertexCount)
            return E_INVALIDARG;

    Mesh mesh;
    mesh.indexCount = indexCount;
    mesh.mode = mode;
    for (unsigned i = 0; i < vertexCount; ++i)
    {
        mesh.center[0] += vertices[i].x / vertexCount;
        mesh.center[1] += vertices[i].y / vertexCount;
        mesh.center[2] += vertices[i].z / vertexCount;
    }

    D3D11_BUFFER_DESC vertexDesc{};
    vertexDesc.ByteWidth = vertexCount * sizeof(Vertex);
    vertexDesc.Usage = D3D11_USAGE_IMMUTABLE;
    vertexDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vertexData{};
    vertexData.pSysMem = vertices;
    HRESULT hr = renderer->device->CreateBuffer(
        &vertexDesc, &vertexData, &mesh.vertices);
    if (FAILED(hr))
        return hr;

    D3D11_BUFFER_DESC indexDesc{};
    indexDesc.ByteWidth = indexCount * sizeof(unsigned);
    indexDesc.Usage = D3D11_USAGE_IMMUTABLE;
    indexDesc.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA indexData{};
    indexData.pSysMem = indices;
    hr = renderer->device->CreateBuffer(
        &indexDesc, &indexData, &mesh.indices);
    if (FAILED(hr))
        return hr;

    mesh.texture = decodedTexture->view;
    for (UINT w = decodedTexture->width, h = decodedTexture->height;; w = std::max(1u, w / 2), h = std::max(1u, h / 2))
    {
        mesh.textureBytes += static_cast<unsigned long long>(w) * h * 4;
        if (w == 1 && h == 1) break;
    }

    mesh.bytes =
        static_cast<unsigned long long>(vertexDesc.ByteWidth) +
        static_cast<unsigned long long>(indexDesc.ByteWidth);
    const auto meshBytes = mesh.bytes;
    (slice ? renderer->sliceMeshes : renderer->meshes).push_back(std::move(mesh));
    renderer->meshBytes += meshBytes;
    return S_OK;
}

struct ImageComScope {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ImageComScope() { if (SUCCEEDED(result)) CoUninitialize(); }
};
static HRESULT DecodeImage(const unsigned char* png, unsigned pngSize, UINT& width, UINT& height, std::vector<BYTE>& pixels)
{
    ImageComScope com;
    if (!png || !pngSize || pngSize > 64 * 1024 * 1024) return E_INVALIDARG;
    ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(
        CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory));
    if (FAILED(hr))
        return hr;

    ComPtr<IWICStream> stream;
    hr = factory->CreateStream(&stream);
    if (FAILED(hr))
        return hr;
    hr = stream->InitializeFromMemory(
        const_cast<BYTE*>(png), static_cast<DWORD>(pngSize));
    if (FAILED(hr))
        return hr;

    ComPtr<IWICBitmapDecoder> decoder;
    hr = factory->CreateDecoderFromStream(
        stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &decoder);
    if (FAILED(hr))
        return hr;
    ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, &frame);
    if (FAILED(hr))
        return hr;
    hr = frame->GetSize(&width, &height);
    if (FAILED(hr) || width == 0 || height == 0 || width > 16384 || height > 16384 || (uint64_t)width * height > 16777216)
        return FAILED(hr) ? hr : E_INVALIDARG;

    WICPixelFormatGUID sourceFormat{};
    hr = frame->GetPixelFormat(&sourceFormat);
    if (FAILED(hr))
        return hr;
    WICPixelFormatGUID rgbaFormat = GUID_WICPixelFormat32bppRGBA;
    ComPtr<IWICBitmapSource> source;
    if (IsEqualGUID(sourceFormat, rgbaFormat))
        hr = frame.As(&source);
    else
    {
        ComPtr<IWICFormatConverter> converter;
        hr = factory->CreateFormatConverter(&converter);
        if (SUCCEEDED(hr))
            hr = converter->Initialize(
                frame.Get(), rgbaFormat, WICBitmapDitherTypeNone, nullptr,
                0.0, WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr))
            hr = converter.As(&source);
    }
    if (FAILED(hr))
        return hr;

    const UINT stride = width * 4;
    const UINT bufferSize = stride * height;
    pixels.resize(bufferSize);
    hr = source->CopyPixels(nullptr, stride, bufferSize, pixels.data());
    if (FAILED(hr))
        return hr;

    return S_OK;
}
API int __cdecl renderer_image_decode(const unsigned char* png, unsigned size, void** result, UINT* width, UINT* height)
{
    if (!result || !width || !height) return E_INVALIDARG;
    *result = nullptr;
    *width = *height = 0;
    try {
        std::vector<BYTE> rgba;
        HRESULT hr = DecodeImage(png, size, *width, *height, rgba);
        if (FAILED(hr)) return hr;
        for (size_t i = 0; i < rgba.size(); i += 4) std::swap(rgba[i], rgba[i + 2]);
        auto copy = CoTaskMemAlloc(rgba.size());
        if (!copy) return E_OUTOFMEMORY;
        memcpy(copy, rgba.data(), rgba.size());
        *result = copy;
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
}
API void __cdecl renderer_image_free(void* pixels) { CoTaskMemFree(pixels); }

API int __cdecl renderer_texture_create(
    Renderer* renderer,
    const unsigned char* png,
    unsigned pngSize,
    DecodedTexture** result,
    int* hasTransparency) try
{
    if (renderer == nullptr || result == nullptr || hasTransparency == nullptr ||
        png == nullptr || pngSize == 0 || pngSize > 64 * 1024 * 1024)
        return E_INVALIDARG;
    *result = nullptr;
    *hasTransparency = 0;

    UINT width = 0, height = 0;
    std::vector<BYTE> pixels;
    HRESULT hr = DecodeImage(png, pngSize, width, height, pixels);
    if (FAILED(hr)) return hr;
    const UINT stride = width * 4;
    bool transparent = false;
    for (size_t index = 3; index < pixels.size(); index += 4)
    {
        if (pixels[index] != 255)
        {
            transparent = true;
            break;
        }
    }

    D3D11_TEXTURE2D_DESC textureDesc{};
    textureDesc.Width = width;
    textureDesc.Height = height;
    textureDesc.MipLevels = 0;
    textureDesc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
    textureDesc.ArraySize = 1;
    textureDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    textureDesc.SampleDesc.Count = 1;
    textureDesc.Usage = D3D11_USAGE_DEFAULT;
    textureDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture;
    hr = renderer->device->CreateTexture2D(&textureDesc, nullptr, &texture);
    if (FAILED(hr))
        return hr;

    renderer->context->UpdateSubresource(texture.Get(), 0, nullptr, pixels.data(), stride, 0);
    auto decoded = new DecodedTexture();
    decoded->width = width;
    decoded->height = height;
    decoded->hasTransparency = transparent;
    hr = renderer->device->CreateShaderResourceView(
        texture.Get(), nullptr, &decoded->view);
    if (FAILED(hr))
    {
        delete decoded;
        return hr;
    }
    renderer->context->GenerateMips(decoded->view.Get());
    *hasTransparency = transparent ? 1 : 0;
    *result = decoded;
    return S_OK;
}
catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }

API void __cdecl renderer_texture_destroy(DecodedTexture* texture)
{
    delete texture;
}

API int __cdecl renderer_camera(
    Renderer* renderer,
    float x, float y, float z,
    float targetX, float targetY, float targetZ)
{
    if (renderer == nullptr)
        return E_INVALIDARG;
    renderer->eye[0] = x;
    renderer->eye[1] = y;
    renderer->eye[2] = z;
    renderer->look[0] = targetX;
    renderer->look[1] = targetY;
    renderer->look[2] = targetZ;
    return S_OK;
}

API int __cdecl renderer_draw(Renderer* renderer)
{
    return renderer == nullptr ? E_INVALIDARG : renderer->Draw(true);
}

API unsigned __cdecl renderer_feature(Renderer* renderer)
{
    return renderer == nullptr ? 0 : (unsigned)renderer->feature;
}

API unsigned long long __cdecl renderer_memory(Renderer* renderer)
{
    if (renderer == nullptr)
        return 0;
    // Color + depth + presentation buffers; excludes compositor-managed caches.
    auto bytes = renderer->meshBytes + (unsigned long long)renderer->width * renderer->height * 12;
    std::unordered_set<ID3D11ShaderResourceView*> textures;
    auto addTextures = [&](const std::vector<Mesh>& meshes)
    {
        for (const auto& mesh : meshes)
            if (mesh.texture && textures.insert(mesh.texture.Get()).second)
                bytes += mesh.textureBytes;
    };
    addTextures(renderer->meshes);
    addTextures(renderer->sliceMeshes);
    return bytes;
}

API int __cdecl renderer_pixels(
    Renderer* renderer, unsigned char* pixels, unsigned size)
{
    if (renderer == nullptr || pixels == nullptr ||
        size < renderer->width * renderer->height * 4)
        return E_INVALIDARG;
    HRESULT hr = renderer->Draw(false);
    if (FAILED(hr))
        return hr;

    auto back = renderer->colorBuffer;
    ComPtr<ID3D11Texture2D> staging;
    D3D11_TEXTURE2D_DESC description;
    back->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    hr = renderer->device->CreateTexture2D(
        &description, nullptr, &staging);
    if (FAILED(hr))
        return hr;
    renderer->context->CopyResource(staging.Get(), back.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = renderer->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
        return hr;
    for (UINT y = 0; y < renderer->height; ++y)
        memcpy(
            pixels + y * renderer->width * 4,
            (const char*)mapped.pData + y * mapped.RowPitch,
            renderer->width * 4);
    renderer->context->Unmap(staging.Get(), 0);
    return S_OK;
}
