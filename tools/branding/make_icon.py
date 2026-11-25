"""Draws the Safety Gate Support app icon: a boom barrier with a striped arm, flat,
in Gränges red (#E51620) and white.

Usage: python3 make_icon.py <output folder>   (requires Pillow)
Copy SafetyGateSupport.ico and SafetyGateSupport-512.png into SafetyGateSupport/Branding.
"""
import math, sys
from PIL import Image, ImageDraw, ImageChops
S=1024; k=S/256
RED=(229,22,32); WHITE=(255,255,255); GREY=(51,51,51)
def poly_rect(p0, angle, length, thick):
    ux,uy=math.cos(angle),math.sin(angle); nx,ny=-uy,ux; h=thick/2
    x,y=p0; return [(x+nx*h,y+ny*h),(x+ux*length+nx*h,y+uy*length+ny*h),(x+ux*length-nx*h,y+uy*length-ny*h),(x-nx*h,y-ny*h)]
def barrier(size, bg, fg, stripe, border=None):
    small=size<=32
    img=Image.new('RGBA',(S,S),(0,0,0,0)); d=ImageDraw.Draw(img)
    pad=0 if small else 8*k
    d.rounded_rectangle([pad,pad,S-pad,S-pad],radius=48*k,fill=bg,outline=border,width=int(3*k) if border else 0)
    sc=lambda pts:[(x*k,y*k) for x,y in pts]
    if small:
        post=(40,104,80,214); base=(26,204,94,222); pivot=(60,124); thick=34; ang=-math.radians(28); L=172
        rest=(190,170,214,214); rbase=(180,204,224,222); pitch=46; sw=20
    else:
        post=(46,100,74,206); base=(34,198,86,210); pivot=(60,122); thick=24; ang=-math.radians(28); L=168
        rest=(192,160,208,206); rbase=(184,198,216,210); pitch=26; sw=12
    # arm (rotated bar) with hazard stripes cut across it
    arm=Image.new('L',(S,S),0); ImageDraw.Draw(arm).polygon(sc(poly_rect(pivot,ang,L,thick)),fill=255)
    stripes=Image.new('L',(S,S),0); sd=ImageDraw.Draw(stripes)
    ux,uy=math.cos(ang),math.sin(ang)
    for i in range(1,int((L-2.2*thick)/pitch)+1):
        c=(pivot[0]+ux*i*pitch, pivot[1]+uy*i*pitch)
        # stripe = parallelogram slanted 45° relative to the arm
        a=ang; s=math.radians(45); w=sw; t=thick
        p=[(c[0]+ux*(-w/2)+(-uy)*(-t),c[1]+uy*(-w/2)+ux*(-t)),
           (c[0]+ux*(w/2)+(-uy)*(-t),c[1]+uy*(w/2)+ux*(-t)),
           (c[0]+ux*(w/2+2*t)+(-uy)*(t),c[1]+uy*(w/2+2*t)+ux*(t)),
           (c[0]+ux*(-w/2+2*t)+(-uy)*(t),c[1]+uy*(-w/2+2*t)+ux*(t))]
        sd.polygon(sc(p),fill=255)
    img.paste(Image.new('RGBA',(S,S),fg+(255,)),(0,0),arm)
    img.paste(Image.new('RGBA',(S,S),stripe+(255,)),(0,0),ImageChops.multiply(arm,stripes))
    # post, base plates and rest post drawn over the arm's root
    d.rounded_rectangle([c*k for c in post],radius=4*k,fill=fg)
    d.rounded_rectangle([c*k for c in base],radius=3*k,fill=fg)
    d.rounded_rectangle([c*k for c in rest],radius=3*k,fill=fg)
    d.rounded_rectangle([c*k for c in rbase],radius=3*k,fill=fg)
    r=(9 if not small else 0)
    if r: d.ellipse([(pivot[0]-r)*k,(pivot[1]-r)*k,(pivot[0]+r)*k,(pivot[1]+r)*k],fill=stripe)
    return img
schemes={'red':(RED,WHITE,RED,None),'white':(WHITE,RED,WHITE,(210,210,210)),'grey':(GREY,RED,WHITE,None)}
if __name__=='__main__':
    import os
    os.chdir(sys.argv[1] if len(sys.argv)>1 else '.')
    sch=schemes['red']; sizes=[16,20,24,32,40,48,64,96,128,256]
    imgs={s:barrier(s,*sch).resize((s,s),Image.LANCZOS) for s in sizes}
    imgs[256].save('SafetyGateSupport.ico',sizes=[(s,s) for s in sizes],append_images=[imgs[s] for s in sizes if s!=256])
    barrier(512,*sch).resize((512,512),Image.LANCZOS).save('SafetyGateSupport-512.png')
