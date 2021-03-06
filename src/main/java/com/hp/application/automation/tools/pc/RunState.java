package com.hp.application.automation.tools.pc;

public enum RunState{

    // The order in this enum is important, changing it could harm other code.
    // please see functions on PCClient waitForRunCompletion and waitForRunState.
    UNDEFINED(""),
    INITIALIZING("Initializing"),
    RUNNING("Running"),
    STOPPING("Stopping"),
    BEFORE_COLLATING_RESULTS("Before Collating Results"),
    COLLATING_RESULTS("Collating Results"),
    BEFORE_CREATING_ANALYSIS_DATA("Before Creating Analysis Data"),
    PENDING_CREATING_ANALYSIS_DATA("Pending Creating Analysis Data"),
    CREATING_ANALYSIS_DATA("Creating Analysis Data"),
    FINISHED("Finished"),
    FAILED_COLLATING_RESULTS("Failed Collating Results"),
    FAILED_CREATING_ANALYSIS_DATA("Failed Creating Analysis Data"),
    CANCELED("Canceled"),
    RUN_FAILURE("Run Failure");

    
    private String value;
    
    private RunState(String value) {
        this.value = value;
    }

    public String value() {
        return value;
    }
    
    public boolean hasFailure () {
        return value.toLowerCase().contains("fail");
    }
    
    public static RunState get(String val){
        for (RunState state : RunState.values()) {
            if (val.equals(state.value()))
                    return state;
        }
        return UNDEFINED;
    }
}
