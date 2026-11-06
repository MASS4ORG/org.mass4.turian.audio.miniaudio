/* miniaudio structs stay private to this library; the managed ABI uses only scalars and pointers. */
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENGINE
#define MA_NO_NODE_GRAPH
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_GENERATION
#include "miniaudio.h"

#if defined(_WIN32)
#define TA_API __declspec(dllexport)
#else
#define TA_API __attribute__((visibility("default")))
#endif

typedef void (*ta_callback)(void*, float*, ma_uint32);
typedef struct {
    ma_context context;
    ma_device device;
    ta_callback callback;
    void* user;
} ta_device;

static void ta_data(ma_device* device, void* output, const void* input, ma_uint32 frames)
{
    ta_device* owner = (ta_device*)device->pUserData;
    (void)input;
    owner->callback(owner->user, (float*)output, frames);
}

TA_API ta_device* ta_device_open(ma_uint32 rate, ma_uint32 period, const char* name,
                                ta_callback callback, void* user)
{
    ta_device* owner = (ta_device*)calloc(1, sizeof(*owner));
    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    ma_device_info* infos;
    ma_uint32 count, i;
    if (!owner) return NULL;
    if (ma_context_init(NULL, 0, NULL, &owner->context) != MA_SUCCESS) goto fail;
    config.playback.format = ma_format_f32;
    config.playback.channels = 2;
    config.sampleRate = rate;
    config.periodSizeInFrames = period;
    config.dataCallback = ta_data;
    config.pUserData = owner;
    if (name && name[0]) {
        if (ma_context_get_devices(&owner->context, &infos, &count, NULL, NULL) != MA_SUCCESS) goto context_fail;
        for (i = 0; i < count; i++) {
            if (strcmp(infos[i].name, name) == 0) {
                config.playback.pDeviceID = &infos[i].id;
                break;
            }
        }
        if (i == count) goto context_fail;
    }
    owner->callback = callback;
    owner->user = user;
    if (ma_device_init(&owner->context, &config, &owner->device) != MA_SUCCESS) goto context_fail;
    if (ma_device_start(&owner->device) != MA_SUCCESS) {
        ma_device_uninit(&owner->device);
        goto context_fail;
    }
    return owner;
context_fail:
    ma_context_uninit(&owner->context);
fail:
    free(owner);
    return NULL;
}

TA_API void ta_device_close(ta_device* owner)
{
    if (!owner) return;
    ma_device_uninit(&owner->device);
    ma_context_uninit(&owner->context);
    free(owner);
}

/* Encoded bytes are pulled on demand from the caller's stream; (size_t)-1 from read reports an I/O failure. */
typedef size_t (*ta_read_proc)(void* user, void* buffer, size_t bytes);
typedef int (*ta_seek_proc)(void* user, ma_int64 offset, int origin);
typedef struct {
    ma_decoder decoder;
    ta_read_proc read;
    ta_seek_proc seek;
    void* user;
} ta_decoder;

static ma_result ta_on_read(ma_decoder* decoder, void* output, size_t bytes, size_t* read)
{
    ta_decoder* owner = (ta_decoder*)decoder->pUserData;
    size_t count = owner->read(owner->user, output, bytes);
    if (count == (size_t)-1) {
        *read = 0;
        return MA_ERROR;
    }
    *read = count;
    return count == 0 && bytes > 0 ? MA_AT_END : MA_SUCCESS;
}

static ma_result ta_on_seek(ma_decoder* decoder, ma_int64 offset, ma_seek_origin origin)
{
    ta_decoder* owner = (ta_decoder*)decoder->pUserData;
    return owner->seek(owner->user, offset, (int)origin) == 0 ? MA_SUCCESS : MA_ERROR;
}

TA_API ta_decoder* ta_decoder_open(ta_read_proc read, ta_seek_proc seek, void* user,
                                   ma_uint32* channels, ma_uint32* rate)
{
    ta_decoder* owner = (ta_decoder*)calloc(1, sizeof(*owner));
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 0, 0);
    if (!owner) return NULL;
    owner->read = read;
    owner->seek = seek;
    owner->user = user;
    if (ma_decoder_init(ta_on_read, ta_on_seek, owner, &config, &owner->decoder) != MA_SUCCESS) {
        free(owner);
        return NULL;
    }
    *channels = owner->decoder.outputChannels;
    *rate = owner->decoder.outputSampleRate;
    return owner;
}

TA_API ma_int64 ta_decoder_read(ta_decoder* owner, float* output, ma_uint64 frames)
{
    ma_uint64 read = 0;
    ma_result result = ma_decoder_read_pcm_frames(&owner->decoder, output, frames, &read);
    return result == MA_SUCCESS || result == MA_AT_END ? (ma_int64)read : -1;
}

TA_API int ta_decoder_seek(ta_decoder* owner, ma_uint64 frame)
{
    return ma_decoder_seek_to_pcm_frame(&owner->decoder, frame);
}

TA_API void ta_decoder_close(ta_decoder* owner)
{
    if (!owner) return;
    ma_decoder_uninit(&owner->decoder);
    free(owner);
}
