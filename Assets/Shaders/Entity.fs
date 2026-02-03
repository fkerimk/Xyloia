#version 330

in vec2 fragTexCoord;
in vec3 fragNormal;
in vec3 fragPosition;

out vec4 finalColor;

uniform sampler2D texture0;
uniform vec4 colDiffuse;

// Lighting uniforms
uniform vec4 lightValues; // (R, G, B, Sky) normalized 0..15

void main() {
    vec4 texelColor = texture(texture0, fragTexCoord);
    if (texelColor.a < 0.1) discard;

    // Calculate light levels using exponential decay (same as chunk shader)
    const float minLight = 0.08735;
    const float scale = 1.0 / (1.0 - minLight);

    float rBase = pow(0.85, 15.0 - lightValues.x);
    float gBase = pow(0.85, 15.0 - lightValues.y);
    float bBase = pow(0.85, 15.0 - lightValues.z);
    float sBase = pow(0.85, 15.0 - lightValues.w);

    float rlf = max(0.0, (rBase - minLight) * scale);
    float glf = max(0.0, (gBase - minLight) * scale);
    float blf = max(0.0, (bBase - minLight) * scale);
    float slf = max(0.0, (sBase - minLight) * scale);

    vec3 light;
    light.r = max(rlf, slf);
    light.g = max(glf, slf);
    light.b = max(blf, slf);
    
    // Minimum ambient brightness
    light = max(light, vec3(0.0));

    finalColor = texelColor * vec4(light, 1.0) * colDiffuse;
}
