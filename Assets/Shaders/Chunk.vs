#version 330

// Input vertex attributes
in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
in vec4 vertexColor; // Packed light data: R=Low byte, G=High byte (normalized 0..1 by Raylib)

// Input uniform values
uniform mat4 mvp;
uniform mat4 matModel;


uniform float animTime;
uniform float meshTime;
uniform float unloadTimer;

// Output vertex attributes (to fragment shader)
out vec2 fragTexCoord;
out vec4 fragColor;
out vec4 fragLight;

void main() {
    
    // Animation: Fade In / Out
    float alpha = 1.0;
    float animSpeed = 4.0;

    // Use animTime ONLY for alpha fade-in (persistent since chunk creation)
    if (animTime < 1.0 / animSpeed) {
        alpha = clamp(animTime * animSpeed, 0.0, 1.0);
    }

    
    if (unloadTimer > 0.0) {
        float fadeOut = 1.0 - clamp(unloadTimer * animSpeed, 0.0, 1.0);
        alpha = min(alpha, fadeOut);
    }
    
    // Raylib sends unsigned byte as normalized float. e.g. 255 -> 1.0.
    int valR = int(round(vertexColor.r * 255.0));
    int valG = int(round(vertexColor.g * 255.0));
    
    // Combine to get the original ushort light value
    int light = valR | (valG << 8);

    int valB = int(round(vertexColor.b * 255.0));
    int valA = int(round(vertexColor.a * 255.0));
    int oldLight = valB | (valA << 8);

    // Fade between old and new light over 0.1s (speed 10)
    float t = clamp(meshTime * 10.0, 0.0, 1.0);

    // If chunk is recently spawned (animTime < 0.1s), skip light fade-in
    if (animTime < 0.1) {
        t = 1.0;
    }

    // Extract channels (4 bits each) and interpolate
    float r = mix(float(oldLight & 0xF), float(light & 0xF), t);
    float g = mix(float((oldLight >> 4) & 0xF), float((light >> 4) & 0xF), t);
    float b = mix(float((oldLight >> 8) & 0xF), float((light >> 8) & 0xF), t);
    float s = mix(float((oldLight >> 12) & 0xF), float((light >> 12) & 0xF), t);

    // Calculate World Position
    vec4 worldPos = matModel * vec4(vertexPosition, 1.0);
    
    // Pass interpolated light levels to fragment shader (0..15)
    fragLight = vec4(r, g, b, s);

    // Pass Alpha in fragColor
    fragColor = vec4(1.0, 1.0, 1.0, alpha);
    
    fragTexCoord = vertexTexCoord;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}