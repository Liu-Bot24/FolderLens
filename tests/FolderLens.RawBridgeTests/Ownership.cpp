#include <stdexcept>
static bool fail_open=false;
static int destroyed=0;
void RawOpenTestHook(){if(fail_open)throw std::runtime_error("open fault");}
void RawDestroyTestHook(){++destroyed;}
#define FOLDERLENS_RAWBRIDGE_TEST
#include "../../src/FolderLens.RawBridge/RawBridge.cpp"

int main()
{
    RawSession* session=nullptr;RawInfo info{};
    fail_open=true;
    if(fl_raw_open(L"unused",256,&session,&info)!=-10002||session||destroyed!=1)return 1;
    fail_open=false;
    if(fl_raw_open(L"nonexistent-raw-fixture",256,&session,&info)==0||session||destroyed!=2)return 2;
    session=new RawSession();fl_raw_close(session);
    return destroyed==3?0:3;
}
