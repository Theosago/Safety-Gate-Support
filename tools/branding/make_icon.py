"""Draws the Safety Gate Support app icon (a light-curtain gate with a signal column).

Usage: python3 make_icon.py <output folder>   (requires Pillow)
Copy SafetyGateSupport.ico and SafetyGateSupport-512.png into SafetyGateSupport/Branding.
"""
from PIL import Image, ImageDraw, ImageFilter
import sys
def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def hexc(h): return tuple(int(h[i:i+2],16) for i in (1,3,5))
TOP,BOT=hexc('#1E86A6'),hexc('#0F2233')
def render(size, simple=False):
    S=1024; k=S/256
    img=Image.new('RGBA',(S,S),(0,0,0,0))
    # background gradient in rounded square
    grad=Image.new('RGBA',(S,S))
    gd=ImageDraw.Draw(grad)
    for y in range(S): gd.line([(0,y),(S,y)],fill=lerp(TOP,BOT,y/S)+(255,))
    mask=Image.new('L',(S,S),0); pad=0 if size<=32 else 8*k
    ImageDraw.Draw(mask).rounded_rectangle([pad,pad,S-pad,S-pad],radius=(48 if size>32 else 40)*k,fill=255)
    img.paste(grad,(0,0),mask)
    d=ImageDraw.Draw(img)
    post=hexc('#E9EEF2'); beam=hexc('#6FE6F5')
    if simple:
        L=(50,36,96,220); R=(160,36,206,220); beams=[88,128,168]; bw=16
    else:
        L=(60,44,98,212); R=(158,44,196,212); beams=[72,100,128,156,184]; bw=8
    # beams with glow
    glow=Image.new('RGBA',(S,S),(0,0,0,0)); g=ImageDraw.Draw(glow)
    for y in beams: g.rounded_rectangle([L[2]*k,(y-bw)*k,R[0]*k,(y+bw)*k],radius=bw*k,fill=beam+(170,))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(10*k if not simple else 6*k)))
    for y in beams: d.rounded_rectangle([L[2]*k,(y-bw/2)*k,R[0]*k,(y+bw/2)*k],radius=bw/2*k,fill=beam+(255,))
    d.rounded_rectangle([c*k for c in L],radius=8*k,fill=post+(255,))
    d.rounded_rectangle([c*k for c in R],radius=8*k,fill=post+(255,))
    if not simple:
        # signal column built into the left post: red, amber, green
        x0,x1=L[0]+6,L[2]-6
        for (y0,y1),col in zip([(52,84),(88,120),(124,156)],['#E5383B','#FFB000','#2DC26B']):
            d.rounded_rectangle([x0*k,y0*k,x1*k,y1*k],radius=5*k,fill=hexc(col)+(255,))
    elif size>=24:
        x0,x1=L[0]+8,L[2]-8
        for (y0,y1),col in zip([(46,78),(82,114),(118,150)],['#E5383B','#FFB000','#2DC26B']):
            d.rounded_rectangle([x0*k,y0*k,x1*k,y1*k],radius=6*k,fill=hexc(col)+(255,))
    return img.resize((size,size),Image.LANCZOS)
sizes=[16,20,24,32,40,48,64,96,128,256]
imgs={s:render(s, simple=s<=32) for s in sizes}
import os; out=sys.argv[1] if len(sys.argv)>1 else "."; os.chdir(out)
render(512).save('SafetyGateSupport-512.png')
# preview sheet
sheet=Image.new('RGBA',(980,330),(244,246,248,255)); x=20
for s in [256,128,64,48,32,24,16]:
    sheet.alpha_composite(imgs[s] if s in imgs else render(s),(x,20+(256-s)//2)); x+=s+20
dark=Image.new('RGBA',(980,60),(32,32,32,255)); x=20
for s in [16,24,32,48]: dark.alpha_composite(imgs[s],(x,(60-s)//2)); x+=s+20
sheet.alpha_composite(dark,(0,270)); sheet.save('icon-preview.png')
imgs[256].save('SafetyGateSupport.ico',sizes=[(s,s) for s in sizes],append_images=[imgs[s] for s in sizes if s!=256])
