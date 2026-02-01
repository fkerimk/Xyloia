#version 330

// Input vertex attributes
in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
in vec4 vertexColor; // Packed light data: R=Low byte, G=High byte (normalized 0..1 by Raylib)

// Input uniform values
uniform mat4 mvp;
uniform mat4 matModel;

uniform vec3 dynLightPos;

// Output vertex attributes (to fragment shader)
out vec2 fragTexCoord;
out vec4 fragColor;

void main() {

    // Raylib sends unsigned byte as normalized float.
    // e.g. 255 -> 1.0.
    
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
    
    // Smooth lighting
    float dist = distance(worldPos.xyz, dynLightPos);
    
    float smoothLight = max(0.0, 15.0 - dist);

    if (smoothLight > 0.0) {
        
        // Unpack Voxel Colors (0..15 range) from the vertex attribute
        float vR = float(r);
        float vG = float(g);
        float vB = float(b);

        // Torch Color Ratios (R=15, G=13, B=10)
        float ratioR = 1.0;
        float ratioG = 13.0 / 15.0;
        float ratioB = 10.0 / 15.0;

        // Calculate theoretical Smooth R, G, B
        float sR = smoothLight * ratioR;
        float sG = smoothLight * ratioG;
        float sB = smoothLight * ratioB;

        float tolerance = 2.5;

        float newR = min(sR, vR + tolerance);
        float newG = min(sG, vG + tolerance);
        float newB = min(sB, vB + tolerance);
        
        // Just for safe keeping if needed downstream, but we will use newR
        r = int(newR); 
        
        vR = newR;
        vG = newG;
        vB = newB;
    }

    // Convert Light Levels to Color Factors (0.0 to 1.0) - from Chunk.cs: AddFace
    float finalR = (smoothLight > 0.0) ? min(smoothLight, float(r) + 2.5) : float(r); 

    float fR = float(r);
    float fG = float(g);
    float fB = float(b);
    
    if (smoothLight > 0.0) {
        float ratioR = 1.0;
        float ratioG = 13.0 / 15.0;
        float ratioB = 10.0 / 15.0;
        
        float sR = smoothLight * ratioR;
        float sG = smoothLight * ratioG;
        float sB = smoothLight * ratioB;
        
        // Increased tolerance to prevent clamping flicker when voxel lags
        float tolerance = 2.0; 
        
        fR = min(sR, fR + tolerance);
        fG = min(sG, fG + tolerance);
        fB = min(sB, fB + tolerance);
    }

    float rlf = pow(0.85, 15.0 - fR);
    float glf = pow(0.85, 15.0 - fG);
    float blf = pow(0.85, 15.0 - fB);
    float slf = pow(0.85, 15.0 - float(s));

    // Combine with Sky Light (s)
    float cr = max(rlf, slf);
    float cg = max(glf, slf);
    float cb = max(blf, slf);
    
    // Minimum brightness
    cr = max(cr, 0.05);
    cg = max(cg, 0.05);
    cb = max(cb, 0.05);

    // Pass final color to fragment shader
    fragColor = vec4(cr, cg, cb, 1.0);
    
    fragTexCoord = vertexTexCoord;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}
