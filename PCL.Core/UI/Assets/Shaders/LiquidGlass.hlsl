// ============================================================================
// [修改文件] LiquidGlass.hlsl / LiquidGlass.ps
// 与 C# 端的常量寄存器保持一一对应，仅重写颜色合成部分。
// 详细修改说明见下方“本次修复”注释。
// ============================================================================
//
// LiquidGlass.hlsl —— PCL Lite 液态玻璃材质（覆盖层版）
//
// 为什么不再采样控件本身：
//   WPF 把带 Effect 的元素先渲染进一张中间位图再贴回来，控件只要落在非整数像素上，
//   这张位图就会被重采样，边缘那圈抗锯齿像素的透明度会被改写（0.52 → 0.74），
//   视觉上就是一圈洗不掉的毛边，连纯直通的着色器也躲不掉。
//   所以这里改成「不碰控件本体」：玻璃作为一层独立的覆盖层画在控件上方，
//   着色器完全不读输入纹理，颜色全部由距离场现算。
//
// [本次修复] 透明混合与深度排序：
//   旧实现把「边缘聚光 / 顶部高光 / 底部阴影」按 alpha 做加权平均，再对总 alpha 做
//   saturate。多层重叠时颜色会被拉灰，总 alpha 超过 1 的部分被硬截断，边缘因此发灰、
//   发脏，还会出现一圈突兀的亮边。
//   现实现严格按「底部阴影 → 边缘聚光 → 顶部高光」从后到前做 over 合成，
//   每一步都同时更新颜色与 alpha，最后统一预乘输出，透明混合在数学上是正确的。
//
// 光照分层（从外到内）：
//   0. 最外 0.9px  —— 交给控件自己的抗锯齿，覆盖层不参与，避免二次描边
//   1. 边缘聚光    —— 整圈贴着边界的一层亮光，靠边最亮；色散作用在这一层
//   2. 顶部亮线    —— 上沿更强的一道高光，交代光从上面来
//   3. 底部内阴影  —— 下沿压一条暗带，这是「玻璃是凸的」最关键的一笔

// 与 C# 端 PixelShaderConstantCallback 一一对应
float2 BorderSize : register(C0);      // 控件尺寸（像素）
float CornerRadius : register(C1);     // 圆角半径
float EffectWidth : register(C2);      // 影响带宽（像素）
float RefractionPower : register(C3);  // 边缘聚光强度 0-1（界面上的「折射强度」）
float Highlight : register(C4);        // 顶部高光强度 0-1（界面上的「高光亮度」）
float4 Tint : register(C5);            // 玻璃色调，a 为融合比例
float Dispersion : register(C6);       // 色散强度 0-1

// 2D 圆角矩形有向距离场：<0 内部，=0 边缘，>0 外部
float SdRoundRect(float2 p, float2 b, float r)
{
    float2 d = abs(p) - (b - r);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 halfSize = BorderSize * 0.5;
    float2 p = (uv - 0.5) * BorderSize;
    float r = min(CornerRadius, min(halfSize.x, halfSize.y));
    float dist = SdRoundRect(p, halfSize, r);

    // 形状抗锯齿：边界内侧 0.9px 渐变，外侧直接透明
    float shapeA = 1.0 - smoothstep(-0.9, 0.0, dist);
    if (shapeA <= 0.002)
        return float4(0.0, 0.0, 0.0, 0.0);

    float depth = max(-dist, 0.0);                            // 距边界向内的深度
    float yUp = saturate(0.5 - p.y / max(BorderSize.y, 1.0)); // 0 = 底，1 = 顶
    float band = saturate(1.0 - depth / max(EffectWidth, 1.0)); // 1 = 贴边，0 = 内部
    float rim = pow(band, 1.70);
    float inner = saturate(depth / max(EffectWidth, 1.0));    // 0 = 边缘，1 = 内侧

    // 圆角处比直边更容易聚光，给角一点额外权重
    float2 q = saturate(abs(p) / max(halfSize, 1.0));
    float corner = pow(saturate(q.x * q.y), 2.0);

    // ---------- 1. 边缘聚光 ----------
    // 靠外偏冷（蓝），靠内转白，形成玻璃的层次
    float3 rimColor = lerp(float3(0.62, 0.80, 1.00), float3(1.00, 1.00, 1.00),
                           saturate(inner * 1.15));
    // 色散：不同通道的聚光带宽略有差异，边缘出现极淡的暖 / 冷分离
    float3 dispersed = float3(
        1.0 + 0.20 * Dispersion * (1.0 - inner),
        1.0,
        1.0 - 0.20 * Dispersion * (1.0 - inner));
    rimColor *= lerp(float3(1.0, 1.0, 1.0), dispersed, rim);
    // 色调融合（Tint.a 很低，只提供极淡的体积感）
    rimColor = lerp(rimColor, Tint.rgb, Tint.a);
    float aRim = rim * (0.20 + 0.80 * RefractionPower) * (0.72 + 0.28 * corner);

    // ---------- 2. 顶部亮线 ----------
    float topW = smoothstep(0.30, 0.95, yUp);
    float topBand = saturate(1.0 - depth / max(EffectWidth * 0.55, 1.0));
    float aTop = pow(topBand, 2.20) * topW * Highlight * 0.42;

    // ---------- 3. 底部内阴影 ----------
    float botW = 1.0 - smoothstep(0.00, 0.45, yUp);
    float botBand = smoothstep(0.20, 0.80, depth) *
                    (1.0 - smoothstep(1.00, max(EffectWidth * 0.75, 2.0), depth));
    float aBot = botBand * botW * (0.10 + 0.30 * Highlight);
    float3 botColor = float3(0.015, 0.035, 0.090);

    // ---------- 4. 透明混合：从后到前逐层 over ----------
    // 直通颜色 + 标量 alpha；每一步：
    //   outA   = srcA + dstA * (1 - srcA)
    //   outRGB = srcRGB * srcA + dstRGB * dstA * (1 - srcA)  ← 用直通颜色表示
    float3 rgb = Tint.rgb;
    float a = Tint.a * (0.35 + 0.65 * rim);

    // 最底层：底部内阴影
    rgb = rgb * (1.0 - aBot) + botColor * aBot;
    a = aBot + a * (1.0 - aBot);

    // 中间层：边缘聚光
    rgb = rgb * (1.0 - aRim) + rimColor * aRim;
    a = aRim + a * (1.0 - aRim);

    // 最上层：顶部高光
    rgb = rgb * (1.0 - aTop) + float3(1.0, 1.0, 1.0) * aTop;
    a = aTop + a * (1.0 - aTop);

    // 顶部整体略微提亮、底部压暗，给平面玻璃一点方向感
    rgb *= lerp(0.94, 1.06, yUp);

    a = saturate(a) * shapeA;
    rgb = saturate(rgb);

    // WPF ShaderEffect 要求预乘 alpha
    return float4(rgb * a, a);
}
