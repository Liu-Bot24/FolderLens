#include <libraw/libraw.h>
#include <atomic>
#include <cstdint>
#include <new>
#include <memory>

// Only this translation unit knows LibRaw's public C++ structures; callers use a fixed POD ABI.
struct RawSession {
    LibRaw raw;
    std::atomic<bool> cancelled{false};
    libraw_processed_image_t* image{nullptr};
    bool thumbnail_unpacked{false};
    ~RawSession() { if(image) LibRaw::dcraw_clear_mem(image);
#ifdef FOLDERLENS_RAWBRIDGE_TEST
        RawDestroyTestHook();
#endif
    }
};
struct RawInfo { uint32_t width,height,thumb_width,thumb_height; int32_t flip; };
struct RawPixels { const unsigned char* data; uint64_t bytes; uint32_t width,height,channels,bits,type; };
static int progress(void* user, enum LibRaw_progress, int, int) { return static_cast<RawSession*>(user)->cancelled.load()?1:0; }
#define EXPORT extern "C" __declspec(dllexport)
EXPORT int fl_raw_open(const wchar_t* path, uint32_t memory_mb, RawSession** output, RawInfo* info) noexcept {
    if(!path || !output || !info || memory_mb<256 || memory_mb>6144) return -10000;
    *output=nullptr;
    try {
        auto session=std::make_unique<RawSession>();
#ifdef FOLDERLENS_RAWBRIDGE_TEST
        RawOpenTestHook();
#endif
        session->raw.imgdata.rawparams.max_raw_memory_mb=memory_mb;
        session->raw.set_progress_handler(progress,session.get());
        int error=session->raw.open_file(path);
        if(error)return error;
        auto& s=session->raw.imgdata.sizes;auto& t=session->raw.imgdata.thumbnail;
        if(static_cast<uint64_t>(s.width)*s.height>300000000ULL)return -10001;
        *info={s.width,s.height,t.twidth,t.theight,s.flip};*output=session.release();return 0;
    } catch (...) {return -10002;}
}
EXPORT int fl_raw_pixels(RawSession* session,int develop,RawPixels* output) noexcept {
    if(!session || !output)return -10000;
    try {
        if(session->image){LibRaw::dcraw_clear_mem(session->image);session->image=nullptr;}
        int error;
        if(develop){
            auto& p=session->raw.imgdata.params;
            p.use_camera_wb=1;p.use_auto_wb=0;p.no_auto_bright=1;p.output_bps=16;p.output_color=1;p.user_qual=3;
            p.half_size=0;p.gamm[0]=1.0/2.4;p.gamm[1]=12.92;
            error=session->raw.unpack();if(error)return error;
            error=session->raw.dcraw_process();if(error)return error;
            session->image=session->raw.dcraw_make_mem_image(&error);
        } else {
            // LibRaw's unpack step is ordered and may only run once per open.
            // The unpacked thumbnail remains owned by LibRaw; output buffers are
            // independent and may be recreated for subsequent preview sizes.
            if(!session->thumbnail_unpacked){
                error=session->raw.unpack_thumb();if(error)return error;
                session->thumbnail_unpacked=true;
            }
            session->image=session->raw.dcraw_make_mem_thumb(&error);
        }
        if(!session->image)return error?error:-10002;
        const auto& i=*session->image;
        *output={i.data,i.data_size,i.width,i.height,i.colors,i.bits,static_cast<uint32_t>(i.type)};
        return 0;
    }catch(...){return -10002;}
}
EXPORT void fl_raw_cancel(RawSession* session) noexcept {if(session)session->cancelled.store(true);}
EXPORT void fl_raw_close(RawSession* session) noexcept {delete session;}
EXPORT uint32_t fl_raw_abi() noexcept {return 1;}
