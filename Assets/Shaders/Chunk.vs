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
uniform float unloadTimer;

// Output vertex attributes (to fragment shader)
out vec2 fragTexCoord;
out vec4 fragColor;

void main() {
    
    // Animation: Fade In / Out
    float alpha = 1.0;
    float animSpeed = 4.0;

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

    // Extract channels (4 bits each)
    int r = (light & 0xF);
    int g = ((light >> 4) & 0xF);
    int b = ((light >> 8) & 0xF);
    int s = ((light >> 12) & 0xF);

    // Calculate World Position
    vec4 worldPos = matModel * vec4(vertexPosition, 1.0);
    
    float fR = float(r);
    float fG = float(g);
    float fB = float(b);
    float fS = float(s);

    float rlf = pow(0.85, 15.0 - fR);
    float glf = pow(0.85, 15.0 - fG);
    float blf = pow(0.85, 15.0 - fB);
    float slf = pow(0.85, 15.0 - fS);

    // Combine with Sky Light (s)
    float cr = max(rlf, slf);
    float cg = max(glf, slf);
    float cb = max(blf, slf);
    
    // Minimum brightness
    cr = max(cr, 0.05);
    cg = max(cg, 0.05);
    cb = max(cb, 0.05);

    // Pass final color to fragment shader
    fragColor = vec4(cr, cg, cb, alpha);
    
    fragTexCoord = vertexTexCoord;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}