#version 330

in vec2 fragTexCoord;
in vec4 fragColor;

out vec4 finalColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

in vec4 fragLight; // r, g, b, s

void main() {

    vec4 texelColor = texture(texture0, fragTexCoord);
    
    // Alpha Cutout
    if (texelColor.a < 0.1) discard;

    // Calculate light factors
    const float minLight = 0.08735;
    const float scale = 1.0 / (1.0 - minLight);

    float rBase = pow(0.85, 15.0 - fragLight.r);
    float gBase = pow(0.85, 15.0 - fragLight.g);
    float bBase = pow(0.85, 15.0 - fragLight.b);
    float sBase = pow(0.85, 15.0 - fragLight.a);

    float rlf = max(0.0, (rBase - minLight) * scale);
    float glf = max(0.0, (gBase - minLight) * scale);
    float blf = max(0.0, (bBase - minLight) * scale);
    float slf = max(0.0, (sBase - minLight) * scale);

    // Combine with Sky Light
    float cr = max(rlf, slf);
    float cg = max(glf, slf);
    float cb = max(blf, slf);
    
    // Minimum brightness
    cr = max(cr, 0.0);
    cg = max(cg, 0.0);
    cb = max(cb, 0.0);

    // Apply lighting
    finalColor = texelColor * vec4(cr, cg, cb, fragColor.a);
}